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

            if (args.Any(a => a.Equals("-forcerelay", StringComparison.OrdinalIgnoreCase)))
            {
                config.ForceRelay = true;
            }

            ParseCommandLine(args, config);

            if (config.ForceRelay)
            {
                foreach (var r in config.ClientRecords)
                {
                    r.ForceRelay = true;
                }
            }

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
            int currentTunnelReuseInterval = cfg.TunnelReuseInterval;
            bool currentAllowRelay = true;
            int currentKeepAlive = cfg.KeepAlive;
            bool missingDevId = !lines.Any(l => l.TrimStart().StartsWith("devid", StringComparison.OrdinalIgnoreCase));
            bool configModified = missingDevId;
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
                    string secName = line.Substring(1, line.Length - 2).Trim();
                    if (secName.Contains('/'))
                    {
                        Console.WriteLine($"[Config] Error: Server name '{secName}' cannot contain '/'. Section ignored.");
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
                        KeepAlive = currentKeepAlive
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
                            if (!val.StartsWith("$HWHash$"))
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
                            if (val.EndsWith(";/file", StringComparison.OrdinalIgnoreCase))
                            {
                                sRec.IsFile = true;
                                sRec.BaseDir = val.Substring(0, val.Length - 6);
                            }
                            else
                            {
                                var parts = val.Split(new[] { ':', '/' });
                                sRec.TargetIp = parts[0];
                                sRec.TargetPort = int.Parse(parts[1]);
                                sRec.IsTcp = parts.Length < 3 || parts[2].ToLower() == "tcp";
                            }
                            break;
                        case "username": case "user": sRec.Username = val; break;
                        case "password": case "pwd": case "pass":
                            if (!val.StartsWith("$HWHash$"))
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
                catch { }
            }
        }

        private static string? ParseClientRecord(string key, string val, AppConfig cfg, string defaultServer = "", int defaultMtu = Constants.DefaultMtu)
        {
            var parts = key.Split('/');
            if (parts.Length > 1 && parts[1].Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[Config] Error: The 'file' protocol cannot be bound to a local port in C-mode. Use the -push/-pull CLI commands instead.");
                return null;
            }

            var rec = new ClientRecord
            {
                Port = int.Parse(parts[0]),
                IsTcp = parts.Length == 1 || parts[1].ToLower() == "tcp",
                Mtu = defaultMtu,
                ForceRelay = cfg.ForceRelay,
                KeepAlive = cfg.KeepAlive
            };

            var valParts = val.Split('@');
            string namePart = valParts[0];
            rec.TargetServer = valParts.Length > 1 ? valParts[1] : defaultServer;
            
            int colonIdx = namePart.IndexOf(':');
            string? newVal = null;
            if (colonIdx > 0)
            {
                rec.TargetName = namePart.Substring(0, colonIdx);
                string passPart = namePart.Substring(colonIdx + 1);
                if (passPart.StartsWith("$HASH256$"))
                {
                    rec.Password = Convert.FromBase64String(passPart.Substring(9));
                }
                else
                {
                    rec.Password = ManagedSHA256.ComputeHashBytes(System.Text.Encoding.UTF8.GetBytes(passPart));
                    string newPassPart = "$HASH256$" + Convert.ToBase64String(rec.Password);
                    newVal = val.Replace(namePart, rec.TargetName + ":" + newPassPart);
                }
            }
            else
            {
                rec.TargetName = namePart;
            }

            if (rec.TargetName.Contains('/'))
            {
                Console.WriteLine($"[Config] Error: Target name '{rec.TargetName}' cannot contain '/'. Entry ignored.");
                return null;
            }

            if (string.IsNullOrEmpty(rec.TargetServer) || rec.TargetServer.Equals("this", StringComparison.OrdinalIgnoreCase))
            {
                rec.IsThis = true;
                cfg.EnableProxy = true;
            }

            cfg.ClientRecords.Add(rec);
            return newVal;
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
                        // S模式命令: name.suffix=target:port/tcp@server or name=path;/file@server
                        var valParts = right.Split('@');
                        string srv = valParts.Length > 1 ? valParts[1] : "localhost";
                        bool isThis = string.IsNullOrEmpty(srv) || srv.Equals("this", StringComparison.OrdinalIgnoreCase);
                        if (isThis) cfg.EnableProxy = true;

                        string srvName = left.Split('.')[0];
                        if (srvName.Contains('/'))
                        {
                            Console.WriteLine($"[Config] Error: Server name '{srvName}' in command line cannot contain '/'. Argument ignored.");
                            continue;
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

                        if (valParts[0].EndsWith(";/file", StringComparison.OrdinalIgnoreCase))
                        {
                            sRec.IsFile = true;
                            sRec.BaseDir = valParts[0].Substring(0, valParts[0].Length - 6);
                        }
                        else
                        {
                            var targetParts = valParts[0].Split(new[] { ':', '/' });
                            sRec.TargetIp = targetParts[0];
                            sRec.TargetPort = int.Parse(targetParts[1]);
                            sRec.IsTcp = targetParts.Length < 3 || targetParts[2].ToLower() == "tcp";
                        }
                        cfg.ServerRecords.Add(sRec);
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