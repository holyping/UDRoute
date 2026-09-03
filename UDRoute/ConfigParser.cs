using System.Text;
using UDRoute.Logging;

namespace UDRoute
{
    public static class ConfigParser
    {
        public static AppConfig Parse(string[] args, bool isServiceMode = false)
        {
            var config = new AppConfig();
            config.DevId = Guid.NewGuid(); // 默认值

            string iniPath = "udroute.ini";
            // 处理命令行参数中的 -c
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-c")
                {
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                    {
                        iniPath = args[i + 1];
                    }
                    break;
                }
            }

            if (File.Exists(iniPath))
            {
                config.ConfigPath = Path.GetFullPath(iniPath);
                ParseIniFile(iniPath, config);
            }
            else
            {
                config.ConfigPath = Path.GetFullPath(iniPath);
            }

            ParseCommandLine(args, config);

            // 处理命令行参数中的 -log
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

            if (string.IsNullOrEmpty(config.LogFile))
            {
                config.LogFile = Path.ChangeExtension(iniPath, ".log");
            }

            // 初始化全局日志系统
            Log.Init(config, isServiceMode);

            if (config.ServerRecords.Count == 0 && config.ClientRecords.Count == 0)
            {
                config.EnableProxy = true;
            }

            if (config.EnableProxy && config.Port == 0)
            {
                config.Port = Constants.DefaultProxyPort;
            }

            return config;
        }

        private static void ParseIniFile(string path, AppConfig cfg)
        {
            var lines = File.ReadAllLines(path);
            string currentServer = "";
            int currentMtu = Constants.DefaultMtu;
            int currentRegInterval = Constants.DefaultRegInterval;
            var currentKcpConfig = new KcpConfig();
            int timeout = Constants.DefaultTimeout;
            bool rewriteIni = false;
            ServerRecord? sRec = null;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith(";")) continue;

                // 移除行末尾的注释（因为本应用场景没有包含 ; 的复杂字符串配置，直接截断即可）
                int commentIdx = line.IndexOf(';');
                if (commentIdx >= 0) line = line.Substring(0, commentIdx).Trim();

                if (line.Length > 2 && line.StartsWith("[") && line.EndsWith("]"))
                {
                    // 发现[name]段，添加Server模式配置并继承当时的局部默认配置
                    cfg.ServerRecords.Add(sRec = new ServerRecord
                    {
                        Name = line.Substring(1, line.Length - 2).Trim(),
                        TargetServer = currentServer,
                        RegInterval = currentRegInterval,
                        Mtu = currentMtu,
                        Timeout = timeout,
                        KcpConfig = currentKcpConfig.Clone()
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
                        case "devid": cfg.DevId = Guid.Parse(val); rewriteIni = false; break;
                        case "regtimeout": cfg.RegTimeout = int.Parse(val); cfg.EnableProxy = true; break;
                        case "reginterval": currentRegInterval = int.Parse(val); break;
                        case "timeout": timeout = int.Parse(val); break;
                        case "loglevel":
                            cfg.LogLevel = val.ToLower() switch
                            {
                                "trace" => LogLevel.Trace,
                                "debug" => LogLevel.Debug,
                                "info" => LogLevel.Info,
                                "warn" or "warning" => LogLevel.Warn,
                                "error" => LogLevel.Error,
                                "none" or "off" => LogLevel.None,
                                _ => Program.IsServiceMode ? LogLevel.Warn : LogLevel.Info
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
                        case "logfile": cfg.LogFile = val; break;
                        case "authfile": cfg.AuthFile = val; break;
                        case "authmode": cfg.AuthMode = val.ToLower(); break;
                        case "username": case "user": cfg.Username = val; break;
                        case "password": case "pwd": case "pass": cfg.Password = val; break;
                        case "kcp": currentKcpConfig.SetProfile(val); break;
                        case "kcpnodelay": currentKcpConfig.NoDelay = val == "1" || bool.Parse(val); break;
                        case "kcpinterval": currentKcpConfig.Interval = int.Parse(val); break;
                        case "kcpresend": currentKcpConfig.Resend = int.Parse(val); break;
                        case "kcpnc": currentKcpConfig.Nc = int.Parse(val); break;
                        case "kcpsndwnd": currentKcpConfig.SndWnd = int.Parse(val); break;
                        case "kcprcvwnd": currentKcpConfig.RcvWnd = int.Parse(val); break;
                        default:
                            // 解析C模式：15389/tcp=rdp@www.pserver.com
                            if (char.IsDigit(key[0]))
                                ParseClientRecord(key, val, cfg, currentServer, currentMtu);
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
                        case "kcp": sRec.KcpConfig.SetProfile(val); break;
                        case "kcpnodelay": sRec.KcpConfig.NoDelay = val == "1" || bool.Parse(val); break;
                        case "kcpinterval": sRec.KcpConfig.Interval = int.Parse(val); break;
                        case "kcpresend": sRec.KcpConfig.Resend = int.Parse(val); break;
                        case "kcpnc": sRec.KcpConfig.Nc = int.Parse(val); break;
                        case "kcpsndwnd": sRec.KcpConfig.SndWnd = int.Parse(val); break;
                        case "kcprcvwnd": sRec.KcpConfig.RcvWnd = int.Parse(val); break;
                        case "target":
                            // 192.168.0.3:3389/tcp
                            var parts = val.Split(new[] { ':', '/' });
                            sRec.TargetIp = parts[0];
                            sRec.TargetPort = int.Parse(parts[1]);
                            sRec.IsTcp = parts.Length < 3 || parts[2].ToLower() == "tcp";
                            break;
                    }
                }
            }

            if (cfg.ServerRecords.Count == 0 && cfg.ClientRecords.Count == 0)
            {
                cfg.EnableProxy = true;
            }

            foreach (var item in cfg.ServerRecords)
            {
                if (item.TargetServer.ToLower() == "this")
                {
                    item.IsThis = true;
                    cfg.EnableProxy = true;
                }
            }

            // 如果ini中没有DevId，尝试写入
            if (rewriteIni)
            {
                var txt = $"DevId={cfg.DevId}\n" + File.ReadAllText(path);
                try { File.WriteAllText(path, txt, Encoding.UTF8); } catch { }
            }
        }

        private static void ParseClientRecord(string key, string val, AppConfig cfg, string defaultServer = "", int defaultMtu = Constants.DefaultMtu)
        {
            var parts = key.Split('/');
            var rec = new ClientRecord
            {
                Port = int.Parse(parts[0]),
                IsTcp = parts.Length == 1 || parts[1].ToLower() == "tcp",
                Mtu = defaultMtu
            };

            var valParts = val.Split('@');
            rec.TargetName = valParts[0];
            rec.TargetServer = valParts.Length > 1 ? valParts[1] : defaultServer;
            if (string.IsNullOrEmpty(rec.TargetServer) || rec.TargetServer.Equals("this", StringComparison.OrdinalIgnoreCase))
            {
                rec.IsThis = true;
                cfg.EnableProxy = true;
            }

            cfg.ClientRecords.Add(rec);
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
                        // S模式命令行: name.suffix=target:port/tcp@server
                        var valParts = right.Split('@');
                        var targetParts = valParts[0].Split(new[] { ':', '/' });
                        string srv = valParts.Length > 1 ? valParts[1] : "localhost";
                        bool isThis = string.IsNullOrEmpty(srv) || srv.Equals("this", StringComparison.OrdinalIgnoreCase);
                        if (isThis) cfg.EnableProxy = true;

                        cfg.ServerRecords.Add(new ServerRecord
                        {
                            Name = left.Split('.')[0],
                            TargetIp = targetParts[0],
                            TargetPort = int.Parse(targetParts[1]),
                            IsTcp = targetParts.Length < 3 || targetParts[2].ToLower() == "tcp",
                            TargetServer = srv,
                            IsThis = isThis,
                            RegInterval = Constants.DefaultRegInterval,
                            Mtu = Constants.DefaultMtu,
                            KcpConfig = new KcpConfig()
                        });
                    }
                }
            }
        }

        public static void CreateTemplate(string path)
        {
            try
            {
                bool isZh = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
                string resName = isZh ? "UDRoute.udroute_template_zh.ini" : "UDRoute.udroute_template_en.ini";

                using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(resName);
                if (stream == null)
                {
                    Console.WriteLine($"Error: Could not find the embedded template resource ({resName}).");
                    return;
                }
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string template = reader.ReadToEnd();
                
                // 自动生成一个新的 DevId 写入模板
                template = template.Replace("{DevId}", Guid.NewGuid().ToString());

                File.WriteAllText(path, template, Encoding.UTF8);
                Console.WriteLine($"Configuration template successfully written to: {Path.GetFullPath(path)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error writing template: {ex.Message}");
            }
        }
    }
}