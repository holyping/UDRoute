using System.Text;
using UDRoute.Logging;

namespace UDRoute
{
    public static class ConfigParser
    {
        public static AppConfig? Parse(string[] args, bool isServiceMode = false)
        {
            var config = new AppConfig();
            config.DevId = Guid.NewGuid(); // 默认值

            // 1. 早期预解析命令行参数中的 -log，确保后续解析过程与初期的日志能够正常输出
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("-log", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    string val = args[i + 1];
                    config.LogLevel = val.ToLower() switch
                    {
                        "trace" => LogLevel.Trace,
                        "debug" => LogLevel.Debug,
                        "info" => LogLevel.Info,
                        "warn" or "warning" => LogLevel.Warn,
                        "error" => LogLevel.Error,
                        "none" or "off" => LogLevel.None,
                        _ => config.LogLevel
                    };
                }
            }
            Log.Init(config, isServiceMode);

            // 2. 检测 -c 参数
            bool hasExplicitConfig = false;
            string requestedIni = "udroute.ini";
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("-c", StringComparison.OrdinalIgnoreCase))
                {
                    hasExplicitConfig = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                    {
                        requestedIni = args[i + 1];
                    }
                    break;
                }
            }

            // 解析配置文件的物理路径（优先当前目录，其次可执行文件所在目录）
            string? resolvedIni = ResolveIniPath(requestedIni);
            if (hasExplicitConfig && resolvedIni == null)
            {
                Log.Error($"[Config] Configuration file '{requestedIni}' was not found in current directory ({Environment.CurrentDirectory}) or executable directory ({AppContext.BaseDirectory}).");
                return null;
            }

            if (!CheckHasTemporyAction(args))
            {
                if (resolvedIni != null && (hasExplicitConfig || File.Exists(resolvedIni)))
                {
                    config.ConfigPath = resolvedIni;
                    if (!ParseIniFile(resolvedIni, config))
                    {
                        return null;
                    }
                }
            }

            if (args.Any(a => a.Equals("-forcerelay", StringComparison.OrdinalIgnoreCase)))
            {
                config.ForceRelay = true;
            }

            if (args.Any(a => a.Equals("-disableipv6", StringComparison.OrdinalIgnoreCase)))
            {
                config.DisableIPv6 = true;
            }

            ParseCommandLine(args, config);

            if (config.ForceRelay)
            {
                foreach (var r in config.ClientRecords)
                {
                    r.ForceRelay = true;
                }
            }

            // 命令行 -log 参数拥有最高优先级，覆盖 ini 中的 loglevel
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("-log", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    string val = args[i + 1];
                    config.LogLevel = val.ToLower() switch
                    {
                        "trace" => LogLevel.Trace,
                        "debug" => LogLevel.Debug,
                        "info" => LogLevel.Info,
                        "warn" or "warning" => LogLevel.Warn,
                        "error" => LogLevel.Error,
                        "none" or "off" => LogLevel.None,
                        _ => config.LogLevel
                    };
                }
            }

            string baseDir = !string.IsNullOrEmpty(config.ConfigPath)
                ? (Path.GetDirectoryName(Path.GetFullPath(config.ConfigPath)) ?? Environment.CurrentDirectory)
                : Environment.CurrentDirectory;

            if (string.IsNullOrEmpty(config.LogFile))
            {
                if (!string.IsNullOrEmpty(config.ConfigPath))
                {
                    config.LogFile = Path.ChangeExtension(config.ConfigPath, ".log");
                }
                else
                {
                    config.LogFile = Path.GetFullPath(Path.Combine(baseDir, "udroute.log"));
                }
            }
            else if (!Path.IsPathRooted(config.LogFile))
            {
                config.LogFile = Path.GetFullPath(Path.Combine(baseDir, config.LogFile));
            }

            if (!string.IsNullOrEmpty(config.AuthFile) && !Path.IsPathRooted(config.AuthFile))
            {
                config.AuthFile = Path.GetFullPath(Path.Combine(baseDir, config.AuthFile));
            }

            // 刷新日志系统配置
            Log.Init(config, isServiceMode);

            if (config.ServerRecords.Count == 0 && config.ClientRecords.Count == 0)
            {
                config.EnableProxy = true;
            }

            if (config.Port == 0)
            {
                config.Port = Constants.DefaultProxyPort;
            }

            if (Log.IsInfoEnabled)
            {
                Log.Info("==================================================");
                Log.Info($"UDRoute Starting... (PID: {Environment.ProcessId})");
                if (!string.IsNullOrEmpty(config.ConfigPath))
                {
                    Log.Info($"Loaded Config: {config.ConfigPath}");
                }
                if (config.ServerRecords.Count > 0)
                {
                    Log.Info($"[S] Server Mode: {config.ServerRecords.Count} service(s) ({string.Join(", ", config.ServerRecords.Select(s => s.Name))})");
                }
                if (config.ClientRecords.Count > 0)
                {
                    Log.Info($"[C] Client Mode: {config.ClientRecords.Count} tunnel(s)");
                }
                if (config.EnableProxy)
                {
                    Log.Info($"[P] Proxy Mode: UDP Port {config.Port}");
                }
                Log.Info($"Log Level: {config.LogLevel}");
                if (config.DisableIPv6) Log.Info("[IPv6] Globally disabled.");
                Log.Info("==================================================");
            }

            ProtocolHelper.DisableIPv6 = config.DisableIPv6;
            return config;
        }

        //检查是否命令行有S和C命令，有的话不加载ini
        static bool CheckHasTemporyAction(string[] args)
        {
            foreach (string arg in args)
            {
                if (arg.Contains('=')) return true;
            }
            return false;
        }

        public static string? ResolveIniPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            // 1. 优先直接判断（支持绝对路径或相对于当前工作目录）
            if (File.Exists(path))
            {
                return Path.GetFullPath(path);
            }

            // 2. 如果不是绝对路径，尝试相对于可执行程序所在目录
            if (!Path.IsPathRooted(path))
            {
                string baseDir = AppContext.BaseDirectory;
                string candidate = Path.Combine(baseDir, path);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            return null;
        }

        private static bool ParseIniFile(string path, AppConfig cfg)
        {
            string iniDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Environment.CurrentDirectory;
            var lines = new List<string>();
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                string? l;
                while ((l = reader.ReadLine()) != null)
                {
                    lines.Add(l);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[Config] Failed to read configuration file '{path}': {ex.Message}");
                return false;
            }

            string currentServer = "";
            int currentMtu = Constants.DefaultMtu;
            int currentRegInterval = Constants.DefaultRegInterval;
            var currentKcpConfig = new KcpConfig();
            int timeout = Constants.DefaultTimeout;
            int currentTunnelReuseInterval = cfg.TunnelReuseInterval;
            bool currentAllowRelay = true;
            int currentKeepAlive = cfg.KeepAlive;
            byte[]? currentAccessPassword = null;
            bool missingDevId = !lines.Any(l => l.TrimStart().StartsWith("devid", StringComparison.OrdinalIgnoreCase));
            bool configModified = missingDevId;
            ServerRecord? sRec = null;

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith(";")) continue;

                // 移除行末尾的注释（针对 target 配置行的 ;/file 做特殊保留处理）
                line = StripComment(line);
                if (string.IsNullOrEmpty(line)) continue;

                if (line.Length > 2 && line.StartsWith("[") && line.EndsWith("]"))
                {
                    string secName = line.Substring(1, line.Length - 2).Trim();
                    if (secName.Contains('/'))
                    {
                        Console.WriteLine(I18n.Text($"[Config] 错误: 服务名 '{secName}' 不能包含 '/'，已忽略该配置段。", $"[Config] Error: Server name '{secName}' cannot contain '/'. Section ignored."));
                        sRec = null; // Ignore subsequent properties for this section
                        continue;
                    }

                    // 发现[name]段，添加Server模式配置并继承当时的局部默认配置
                    cfg.ServerRecords.Add(sRec = new ServerRecord
                    {
                        Name = secName,
                        TargetServer = currentServer,
                        RegInterval = currentRegInterval,
                        Mtu = currentMtu,
                        Timeout = timeout,
                        TunnelReuseInterval = currentTunnelReuseInterval,
                        AllowRelay = currentAllowRelay,
                        KcpConfig = currentKcpConfig.Clone(),
                        KeepAlive = currentKeepAlive,
                        AccessPassword = currentAccessPassword
                    });
                    continue;
                }

                int eqIdx = line.IndexOf('=');
                if (eqIdx == -1) continue;

                string key = line.Substring(0, eqIdx).Trim().ToLower();
                string val = line.Substring(eqIdx + 1).Trim();

                if (sRec == null)
                {
                    // 全局配置（顺序向下生效）
                    switch (key)
                    {
                        case "port": cfg.Port = int.Parse(val); cfg.EnableProxy = true; break;
                        case "wanport": cfg.WanPort = int.Parse(val); break;
                        case "server": currentServer = val; break;
                        case "mtu": currentMtu = int.Parse(val); break;
                        case "devname": cfg.DevName = val; break;
                        case "devid": cfg.DevId = Guid.Parse(val); break;
                        case "regtimeout": cfg.RegTimeout = int.Parse(val); cfg.EnableProxy = true; break;
                        case "idlethreshold" or "tunnelidle": cfg.IdleThreshold = int.Parse(val); cfg.EnableProxy = true; break;
                        case "probetimeout" or "handshaketimeout": cfg.ProbeTimeout = int.Parse(val); cfg.EnableProxy = true; break;
                        case "maxrecentrequests" or "maxsize":
                            int parsedMaxRecent = int.Parse(val);
                            cfg.MaxRecentRequests = parsedMaxRecent < Constants.MinMaxRecentRequests ? Constants.MinMaxRecentRequests : parsedMaxRecent;
                            break;
                        case "reginterval": currentRegInterval = int.Parse(val); break;
                        case "timeout": timeout = int.Parse(val); break;
                        case "tunnelreuseinterval" or "tunnelreuse":
                            currentTunnelReuseInterval = int.Parse(val);
                            cfg.TunnelReuseInterval = currentTunnelReuseInterval;
                            break;
                        case "keepalive" or "keepaliveinterval":
                            if (int.TryParse(val, out int parsedKa))
                            {
                                cfg.KeepAlive = parsedKa;
                                currentKeepAlive = parsedKa;
                            }
                            break;
                        case "loglevel":
                            cfg.LogLevel = val.ToLower() switch
                            {
                                "trace" => LogLevel.Trace,
                                "debug" => LogLevel.Debug,
                                "info" => LogLevel.Info,
                                "warn" or "warning" => LogLevel.Warn,
                                "error" => LogLevel.Error,
                                "none" or "off" => LogLevel.None,
                                _ => LogLevel.Info
                            };
                            break;
                        case "logtype" or "log":
                            cfg.LogType = val.ToLower() switch
                            {
                                "console" => LogType.Console,
                                "eventlog" or "event" or "syslog" => LogType.EventLog,
                                "file" => LogType.File,
                                _ => LogType.Default
                            };
                            break;
                        case "logfile": cfg.LogFile = Path.IsPathRooted(val) ? Path.GetFullPath(val) : Path.GetFullPath(Path.Combine(iniDir, val)); break;
                        case "authfile": cfg.AuthFile = Path.IsPathRooted(val) ? Path.GetFullPath(val) : Path.GetFullPath(Path.Combine(iniDir, val)); break;
                        case "authmode":
                            cfg.AuthMode = val.ToLower() switch
                            {
                                "strict" => AuthMode.Strict,
                                "optional" => AuthMode.Optional,
                                _ => AuthMode.None
                            };
                            break;
                        case "allowunauthrelay" or "allowanonymousrelay":
                            cfg.AllowUnauthRelay = val.ToLower() switch
                            {
                                "allow" or "true" or "1" or "yes" => AllowUnauthRelay.Allow,
                                "deny" or "false" or "0" or "no" => AllowUnauthRelay.Deny,
                                _ => AllowUnauthRelay.Default
                            };
                            break;
                        case "allowrelay":
                            currentAllowRelay = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase) || val.Equals("allow", StringComparison.OrdinalIgnoreCase);
                            cfg.AllowUnauthRelay = currentAllowRelay ? AllowUnauthRelay.Allow : AllowUnauthRelay.Deny;
                            break;
                        case "maxunauthnamesperuser" or "maxunauthnames" or "maxunauthperuser": cfg.MaxUnauthNamesPerUser = int.Parse(val); break;
                        case "maxunauthnamestotal" or "maxtotalunauthnames": cfg.MaxUnauthNamesTotal = int.Parse(val); break;
                        case "username": case "user": cfg.Username = val; break;
                        case "password": case "pwd": case "pass":
                            if (!val.StartsWith("_HWHash_", StringComparison.OrdinalIgnoreCase))
                            {
                                string protectedVal = ConfigProtector.ComputeHWHash(val);
                                lines[i] = lines[i].Replace(val, protectedVal);
                                configModified = true;
                                val = protectedVal;
                            }
                            try
                            {
                                cfg.Password = Convert.FromBase64String(val.Substring(8));
                            }
                            catch
                            {
                                Log.Warn($"[Config] Global password Base64 format error");
                                cfg.Password = null;
                            }
                            break;
                        case "accesspassword" or "accesspwd" or "accesspass":
                            if (val.StartsWith("_HASH256_", StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    cfg.AccessPassword = currentAccessPassword = Convert.FromBase64String(val.Substring(9));
                                }
                                catch
                                {
                                    Log.Warn($"[Config] Global access password Base64 format error");
                                    cfg.AccessPassword = currentAccessPassword = null;
                                }
                            }
                            else
                            {
                                byte[] hashBytes = ManagedSHA256.ComputeHashBytes(Encoding.UTF8.GetBytes(val));
                                string protectedVal = "_HASH256_" + Convert.ToBase64String(hashBytes);
                                lines[i] = lines[i].Replace(val, protectedVal);
                                configModified = true;
                                cfg.AccessPassword = currentAccessPassword = hashBytes;
                            }
                            break;
                        case "controllerpassword" or "controllerpwd" or "controllerpass":
                            if (val.StartsWith("_HASH256_", StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    cfg.ControllerPassword = Convert.FromBase64String(val.Substring(9));
                                }
                                catch
                                {
                                    Log.Warn($"[Config] Global controller password Base64 format error");
                                    cfg.ControllerPassword = null;
                                }
                            }
                            else
                            {
                                byte[] hashBytes = ManagedSHA256.ComputeHashBytes(Encoding.UTF8.GetBytes(val));
                                string protectedVal = "_HASH256_" + Convert.ToBase64String(hashBytes);
                                lines[i] = lines[i].Replace(val, protectedVal);
                                configModified = true;
                                cfg.ControllerPassword = hashBytes;
                            }
                            break;
                        case "controllerport":
                            if (int.TryParse(val, out int cp)) cfg.ControllerPort = cp;
                            break;
                        case "kcp": currentKcpConfig.SetProfile(val); break;
                        case "kcpnodelay": currentKcpConfig.NoDelay = val == "1" || bool.Parse(val); break;
                        case "kcpinterval": currentKcpConfig.Interval = int.Parse(val); break;
                        case "kcpresend": currentKcpConfig.Resend = int.Parse(val); break;
                        case "kcpnc": currentKcpConfig.Nc = int.Parse(val); break;
                        case "kcpsndwnd": currentKcpConfig.SndWnd = int.Parse(val); break;
                        case "kcprcvwnd": currentKcpConfig.RcvWnd = int.Parse(val); break;
                        case "forcerelay":
                            cfg.ForceRelay = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "disableipv6":
                            cfg.DisableIPv6 = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                            break;
                        default:
                            // 解析C模式：15389/tcp=rdp@www.pserver.com
                            if (char.IsDigit(key[0]))
                            {
                                string? newVal = ParseClientRecord(key, val, cfg, currentServer, currentMtu);
                                if (newVal != null)
                                {
                                    lines[i] = lines[i].Replace(val, newVal);
                                    configModified = true;
                                }
                            }
                            break;
                    }
                }
                else
                {
                    // S模式具体段
                    switch (key)
                    {
                        case "server":
                            sRec.TargetServer = val;
                            sRec.IsThis = string.IsNullOrEmpty(val) || val.Equals("this", StringComparison.OrdinalIgnoreCase);
                            if (sRec.IsThis) cfg.EnableProxy = true;
                            break;
                        case "mtu": sRec.Mtu = int.Parse(val); break;
                        case "reginterval": sRec.RegInterval = int.Parse(val); break;
                        case "keepalive" or "keepaliveinterval":
                            if (int.TryParse(val, out int sKa)) sRec.KeepAlive = sKa;
                            break;
                        case "tunnelreuseinterval" or "tunnelreuse": sRec.TunnelReuseInterval = int.Parse(val); break;
                        case "allowrelay": sRec.AllowRelay = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "readonly": sRec.ReadOnly = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "kcp": sRec.KcpConfig.SetProfile(val); break;
                        case "kcpnodelay": sRec.KcpConfig.NoDelay = val == "1" || bool.Parse(val); break;
                        case "kcpinterval": sRec.KcpConfig.Interval = int.Parse(val); break;
                        case "kcpresend": sRec.KcpConfig.Resend = int.Parse(val); break;
                        case "kcpnc": sRec.KcpConfig.Nc = int.Parse(val); break;
                        case "kcpsndwnd": sRec.KcpConfig.SndWnd = int.Parse(val); break;
                        case "kcprcvwnd": sRec.KcpConfig.RcvWnd = int.Parse(val); break;
                        case "target":
                            if (val.StartsWith("\"") && val.EndsWith("\"") && val.Length >= 2)
                            {
                                val = val.Substring(1, val.Length - 2).Trim();
                            }
                            if (val.EndsWith(";/file", StringComparison.OrdinalIgnoreCase))
                            {
                                sRec.IsFile = true;
                                sRec.BaseDir = val.Substring(0, val.Length - 6).TrimEnd('\\', '/');
                            }
                            else
                            {
                                var parts = val.Split(new[] { ':', '/' });
                                if (parts.Length >= 2 && int.TryParse(parts[1], out int port))
                                {
                                    sRec.TargetIp = parts[0];
                                    sRec.TargetPort = port;
                                    sRec.IsTcp = parts.Length < 3 || parts[2].ToLower() == "tcp";
                                }
                                else
                                {
                                    Log.Error($"[Config] Invalid target format '{val}' in section '[{sRec.Name}]'. Expected 'ip:port[/tcp|udp]' or 'path;/file'.");
                                }
                            }
                            break;
                        case "username": case "user": sRec.Username = val; break;
                        case "password": case "pwd": case "pass":
                            if (!val.StartsWith("_HWHash_", StringComparison.OrdinalIgnoreCase))
                            {
                                string protectedVal = ConfigProtector.ComputeHWHash(val);
                                lines[i] = lines[i].Replace(val, protectedVal);
                                configModified = true;
                                val = protectedVal;
                            }
                            try
                            {
                                sRec.Password = Convert.FromBase64String(val.Substring(8));
                            }
                            catch
                            {
                                Log.Warn($"[Config] Server '{sRec.Name}' password Base64 format error");
                                sRec.Password = null;
                            }
                            break;
                        case "accesspassword" or "accesspwd" or "accesspass":
                            if (val.StartsWith("_HASH256_", StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    sRec.AccessPassword = Convert.FromBase64String(val.Substring(9));
                                }
                                catch
                                {
                                    Log.Warn($"[Config] Server '{sRec.Name}' access password Base64 format error");
                                    sRec.AccessPassword = null;
                                }
                            }
                            else
                            {
                                byte[] hashBytes = ManagedSHA256.ComputeHashBytes(Encoding.UTF8.GetBytes(val));
                                string protectedVal = "_HASH256_" + Convert.ToBase64String(hashBytes);
                                lines[i] = lines[i].Replace(val, protectedVal);
                                configModified = true;
                                sRec.AccessPassword = hashBytes;
                            }
                            break;
                    }
                }
            }

            if (cfg.ServerRecords.Count == 0 && cfg.ClientRecords.Count == 0)
            {
                cfg.EnableProxy = true;
            }
            foreach (var r in cfg.ClientRecords)
            {
                r.ForceRelay = cfg.ForceRelay;
            }

            foreach (var item in cfg.ServerRecords)
            {
                if (item.TargetServer.ToLower() == "this")
                {
                    item.IsThis = true;
                    cfg.EnableProxy = true;
                }
                
                // 统一为 file 协议追加后缀，以在 Proxy 处与常规 tcp/udp 隔离
                if (item.IsFile && !item.Name.EndsWith("/file", StringComparison.OrdinalIgnoreCase))
                {
                    item.Name += "/file";
                }
            }

            // 将发生变化的行写入配置文件
            if (configModified)
            {
                try
                {
                    if (missingDevId && cfg.DevId != Guid.Empty)
                    {
                        var newLines = new List<string> { $"DevId={cfg.DevId}" };
                        newLines.AddRange(lines);
                        File.WriteAllLines(path, newLines, Encoding.UTF8);
                    }
                    else
                    {
                        File.WriteAllLines(path, lines, Encoding.UTF8);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"[Config] Could not update configuration file '{path}': {ex.Message}");
                }
            }

            return true;
        }

        public static (ClientRecord? Record, string? ProtectedValue) ParseClientEndpoint(string key, string val, AppConfig cfg, string defaultServer = "", int defaultMtu = Constants.DefaultMtu)
        {
            var parts = key.Split('/');
            if (parts.Length > 1 && parts[1].Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(I18n.Text("[Config] 错误: C模式下 'file' 协议不能绑定到本地端口。请使用 -push/-pull 命令。", "[Config] Error: The 'file' protocol cannot be bound to a local port in C-mode. Use the -push/-pull CLI commands instead."));
                return (null, null);
            }

            if (!int.TryParse(parts[0], out int port) || port <= 0 || port > 65535)
            {
                return (null, null);
            }

            var rec = new ClientRecord
            {
                Port = port,
                IsTcp = parts.Length == 1 || parts[1].ToLower() == "tcp",
                Mtu = defaultMtu,
                ForceRelay = cfg.ForceRelay,
                KeepAlive = cfg.KeepAlive
            };

            var valParts = val.Split('@');
            string namePart = valParts[0];
            rec.TargetServer = valParts.Length > 1 ? valParts[1] : (string.IsNullOrEmpty(defaultServer) ? cfg.Server : defaultServer);
            
            int colonIdx = namePart.IndexOf(':');
            string? newVal = null;
            if (colonIdx > 0)
            {
                rec.TargetName = namePart.Substring(0, colonIdx);
                string passPart = namePart.Substring(colonIdx + 1);
                if (passPart.StartsWith("_HASH256_", StringComparison.OrdinalIgnoreCase))
                {
                    rec.Password = Convert.FromBase64String(passPart.Substring(9));
                }
                else
                {
                    rec.Password = ManagedSHA256.ComputeHashBytes(System.Text.Encoding.UTF8.GetBytes(passPart));
                    string newPassPart = "_HASH256_" + Convert.ToBase64String(rec.Password);
                    newVal = val.Replace(namePart, rec.TargetName + ":" + newPassPart);
                }
            }
            else
            {
                rec.TargetName = namePart;
            }

            if (rec.TargetName.Contains('/'))
            {
                Console.WriteLine(I18n.Text($"[Config] 错误: 目标服务名 '{rec.TargetName}' 不能包含 '/'，已忽略该条目。", $"[Config] Error: Target name '{rec.TargetName}' cannot contain '/'. Entry ignored."));
                return (null, null);
            }

            if (string.IsNullOrEmpty(rec.TargetServer) || rec.TargetServer.Equals("this", StringComparison.OrdinalIgnoreCase))
            {
                rec.IsThis = true;
                cfg.EnableProxy = true;
            }

            return (rec, newVal);
        }

        private static string? ParseClientRecord(string key, string val, AppConfig cfg, string defaultServer = "", int defaultMtu = Constants.DefaultMtu)
        {
            var (rec, newVal) = ParseClientEndpoint(key, val, cfg, defaultServer, defaultMtu);
            if (rec != null)
            {
                cfg.ClientRecords.Add(rec);
            }
            return newVal;
        }

        public static ServerRecord? ParseServerEndpoint(string left, string right, AppConfig cfg)
        {
            var valParts = right.Split('@');
            string srv = valParts.Length > 1 ? valParts[1] : (string.IsNullOrEmpty(cfg.Server) ? "localhost" : cfg.Server);
            bool isThis = string.IsNullOrEmpty(srv) || srv.Equals("this", StringComparison.OrdinalIgnoreCase);
            if (isThis) cfg.EnableProxy = true;

            string srvName = left.Split('.')[0];
            if (srvName.Contains('/'))
            {
                Console.WriteLine(I18n.Text($"[Config] 错误: 命令行服务名 '{srvName}' 不能包含 '/'，已忽略该参数。", $"[Config] Error: Server name '{srvName}' in command line cannot contain '/'. Argument ignored."));
                return null;
            }

            var sRec = new ServerRecord
            {
                Name = srvName,
                TargetServer = srv,
                IsThis = isThis,
                RegInterval = Constants.DefaultRegInterval,
                Mtu = Constants.DefaultMtu,
                KcpConfig = new KcpConfig(),
                KeepAlive = cfg.KeepAlive
            };

            string targetPart = valParts[0];
            if (targetPart.StartsWith("\"") && targetPart.EndsWith("\"") && targetPart.Length >= 2)
            {
                targetPart = targetPart.Substring(1, targetPart.Length - 2).Trim();
            }
            if (targetPart.EndsWith(";/file", StringComparison.OrdinalIgnoreCase))
            {
                sRec.IsFile = true;
                sRec.BaseDir = targetPart.Substring(0, targetPart.Length - 6).TrimEnd('\\', '/');
                if (!sRec.Name.EndsWith("/file", StringComparison.OrdinalIgnoreCase))
                {
                    sRec.Name += "/file";
                }
            }
            else
            {
                var targetParts = targetPart.Split(new[] { ':', '/' });
                if (targetParts.Length >= 2 && int.TryParse(targetParts[1], out int port))
                {
                    sRec.TargetIp = targetParts[0];
                    sRec.TargetPort = port;
                    sRec.IsTcp = targetParts.Length < 3 || targetParts[2].ToLower() == "tcp";
                }
                else
                {
                    Console.WriteLine(I18n.Text($"[Config] 错误: 目标格式 '{targetPart}' 无效。应为 'ip:port[/tcp|udp]' 或 'path;/file'。", $"[Config] Error: Invalid target format '{targetPart}'. Expected 'ip:port[/tcp|udp]' or 'path;/file'."));
                    return null;
                }
            }

            return sRec;
        }

        private static void ParseCommandLine(string[] args, AppConfig cfg)
        {
            foreach (var arg in args)
            {
                if (arg.StartsWith("-")) continue; // 跳过 flags

                int eqIdx = arg.IndexOf('=');
                if (eqIdx > 0)
                {
                    string left = arg.Substring(0, eqIdx);
                    string right = arg.Substring(eqIdx + 1);

                    if (char.IsDigit(left[0]))
                    {
                        ParseClientRecord(left, right, cfg);
                    }
                    else
                    {
                        var sRec = ParseServerEndpoint(left, right, cfg);
                        if (sRec != null)
                        {
                            cfg.ServerRecords.Add(sRec);
                        }
                    }
                }
            }
        }

        private static string StripComment(string line)
        {
            string trimmed = line.TrimStart();
            bool isTargetLine = false;
            int eqIdx = trimmed.IndexOf('=');
            if (eqIdx > 0)
            {
                string key = trimmed.Substring(0, eqIdx).Trim();
                if (key.Equals("target", StringComparison.OrdinalIgnoreCase))
                {
                    isTargetLine = true;
                }
            }

            bool inQuotes = false;
            bool foundProtocol = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    continue;
                }

                if (!inQuotes && c == ';')
                {
                    if (isTargetLine && !foundProtocol)
                    {
                        if (i + 6 <= line.Length && line.Substring(i, 6).Equals(";/file", StringComparison.OrdinalIgnoreCase))
                        {
                            string prefix = line.Substring(0, i);
                            bool hasProtoBefore = prefix.Contains("/tcp", StringComparison.OrdinalIgnoreCase)
                                               || prefix.Contains("/udp", StringComparison.OrdinalIgnoreCase)
                                               || prefix.Contains("/file", StringComparison.OrdinalIgnoreCase);

                            if (!hasProtoBefore)
                            {
                                foundProtocol = true;
                                i += 5;
                                continue;
                            }
                        }
                    }

                    return line.Substring(0, i).Trim();
                }
            }

            return line.Trim();
        }
    }
}