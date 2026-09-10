using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UDRoute.Logging; 

namespace UDRoute
{
    public class AuthManager : IDisposable
    {
        private FileSystemWatcher? _watcher;
        private string _pwdFilePath;
        
        // 存储 Username -> Hash 的字典，大小写不敏感
        private Dictionary<string, string> _users = new(StringComparer.OrdinalIgnoreCase);
        
        public bool HasUsers => _users.Count > 0;
        public int UserCount => _users.Count;
        
        // 用于防止多个变更事件同时触发导致的并发问题
        private readonly object _processLock = new object();
        
        // 区分明文与密文的特殊前缀
        private const string HashPrefix = "_HASH256_";

        /// <summary>
        /// 初始化认证管理器
        /// </summary>
        /// <param name="configFilePath">主配置文件的绝对路径 (如 /etc/udroute/instA.ini)</param>
        /// <param name="explicitPwdFile">可选：指定的密码文件路径</param>
        public AuthManager(string configFilePath, string? explicitPwdFile = null)
        {
            if (!string.IsNullOrWhiteSpace(explicitPwdFile))
            {
                _pwdFilePath = Path.GetFullPath(explicitPwdFile);
            }
            else
            {
                // 默认策略：将配置文件的后缀改成 .pwd
                _pwdFilePath = Path.ChangeExtension(Path.GetFullPath(configFilePath), ".pwd");
            }
        }

        public void Start()
        {
            if (File.Exists(_pwdFilePath))
            {
                LoadAndProcessFile();
            }
            else
            {
                Log.Info($"[Auth] 密码文件不存在: {_pwdFilePath}，将允许无认证或等待文件创建。");
            }

            WatchDirectory();
        }

        private void WatchDirectory()
        {
            string? dir = Path.GetDirectoryName(_pwdFilePath);
            string file = Path.GetFileName(_pwdFilePath);

            // 如果目录不存在，无法监听（可以由外部确保目录存在）
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            _watcher = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime
            };
            
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.EnableRaisingEvents = true;
            
            Log.Info($"[Auth] 开始监听密码文件变更: {_pwdFilePath}");
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            // 延迟 500ms：让 vi/nano 等编辑器有足够时间释放文件锁
            Task.Run(async () => 
            {
                await Task.Delay(500); 
                LoadAndProcessFile();
            });
        }

        private void LoadAndProcessFile()
        {
            // 确保并发保存时只有一次处理
            lock (_processLock)
            {
                List<string> rawLines = new List<string>();
                bool readSuccess = false;

                // 读文件（带重试机制，防止编辑器占用）
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        rawLines = new List<string>(File.ReadAllLines(_pwdFilePath, Encoding.UTF8));
                        readSuccess = true;
                        break;
                    }
                    catch (IOException)
                    {
                        Thread.Sleep(200); // 文件被锁，等 200ms
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[Auth] 读取密码文件失败: {ex.Message}");
                        break;
                    }
                }

                if (!readSuccess) return;

                var newUsersDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var newLines = new List<string>();
                bool needsRewrite = false;

                foreach (var line in rawLines)
                {
                    // 忽略空行和注释
                    if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#"))
                    {
                        newLines.Add(line);
                        continue;
                    }

                    var parts = line.Split(':', 2);
                    if (parts.Length != 2)
                    {
                        newLines.Add(line); // 格式不对，原样保留
                        continue;
                    }

                    string username = parts[0].Trim();
                    string secret = parts[1].Trim();
                    string finalHash = secret;

                    // 检查是否为明文：只要不以 _HASH256_ 开头，就属于明文密码
                    if (!secret.StartsWith(HashPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        // 计算 Hash (密码B) 并自动回写
                        byte[] bBytes = ManagedSHA256.ComputeHashBytes(Encoding.UTF8.GetBytes(secret));
                        finalHash = HashPrefix + Convert.ToBase64String(bBytes);
                        newLines.Add($"{username}:{finalHash}"); // 替换为哈希行
                        needsRewrite = true;
                    }
                    else
                    {
                        newLines.Add(line); // 已是哈希，直接加入
                    }

                    // 存入内存字典
                    newUsersDict[username] = finalHash;
                }

                // 原子替换字典，防止多线程访问脏数据
                _users = newUsersDict;

                // 自动回写机制
                if (needsRewrite)
                {
                    if (_watcher != null) _watcher.EnableRaisingEvents = false; // 【防死循环关键】暂停监听

                    for (int i = 0; i < 3; i++)
                    {
                        try
                        {
                            File.WriteAllLines(_pwdFilePath, newLines, Encoding.UTF8);
                            Log.Info($"[Auth] 已自动将 {_pwdFilePath} 中的明文密码转化为 HASH256 存储。");
                            break;
                        }
                        catch (UnauthorizedAccessException)
                        {
                            // 【权限降级处理】
                            Log.Error($"[Auth] 无权写入 {_pwdFilePath}！保留其明文状态，但密码仍可正常使用。");
                            break; // 权限问题重试也没用，直接跳出
                        }
                        catch (IOException)
                        {
                            Thread.Sleep(200);
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"[Auth] 自动回写密码文件失败: {ex.Message}");
                            break;
                        }
                    }

                    if (_watcher != null) _watcher.EnableRaisingEvents = true; // 恢复监听
                }
                else
                {
                    Log.Info($"[Auth] 密码文件加载完成，共 {_users.Count} 个用户。");
                }
            }
        }

        /// <summary>
        /// 提供给外部的鉴权接口
        /// </summary>
        public (bool success, string reason) Authenticate(string username, string inputPassword)
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(inputPassword)) return (false, "Username or password empty");

            if (_users.TryGetValue(username, out string? storedHash))
            {
                if (storedHash == null) return (false, "User hash is null");

                if (inputPassword.StartsWith("_HW_"))
                {
                    var parts = inputPassword.Substring(4).Split('|');
                    if (parts.Length == 3 && long.TryParse(parts[0], out long ts))
                    {
                        // 300秒抗重放攻击验证（允许5分钟时钟偏差与网络延迟）
                        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        if (Math.Abs(now - ts) > 300)
                        {
                            Log.Warn($"[Auth] 检测到重放攻击或时间偏移过大: 用户 {username}");
                            return (false, "Time drift > 300s or replay attack");
                        }

                        string t1 = parts[1];
                        string dReceived = parts[2];
                        byte[] tsBytes = Encoding.UTF8.GetBytes(ts.ToString());

                        // 算法：t1 + B = C, ts + C = D
                        // storedHash 即为密码 B (_HASH256_...)
                        byte[] cBytes = ManagedSHA256.ComputeHashBytes(Encoding.UTF8.GetBytes(t1 + storedHash));
                        byte[] tsAndC = new byte[tsBytes.Length + 32];
                        Buffer.BlockCopy(tsBytes, 0, tsAndC, 0, tsBytes.Length);
                        Buffer.BlockCopy(cBytes, 0, tsAndC, tsBytes.Length, 32);
                        byte[] dBytes = ManagedSHA256.ComputeHashBytes(tsAndC);
                        string expectedD = Convert.ToBase64String(dBytes);

                        if (string.Equals(dReceived, expectedD, StringComparison.OrdinalIgnoreCase))
                            return (true, "OK");
                        else
                            return (false, "Hash mismatch");
                    }
                    return (false, "Invalid _HW_ payload format");
                }

                // 将用户传入的明文密码计算为 HASH256 进行比对
                byte[] inputBBytes = ManagedSHA256.ComputeHashBytes(Encoding.UTF8.GetBytes(inputPassword));
                string inputHash = HashPrefix + Convert.ToBase64String(inputBBytes);

                if (string.Equals(storedHash, inputHash, StringComparison.OrdinalIgnoreCase))
                {
                    return (true, "OK");
                }
                else
                {
                    return (false, "Plaintext hash mismatch");
                }
            }
            return (false, "User not found");
        }

        private string ComputeSha256(string input)
        {
            return ManagedSHA256.ComputeHash(input);
        }

        public void Dispose()
        {
            _watcher?.Dispose();
        }
    }
}

