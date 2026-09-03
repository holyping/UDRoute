using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UDRoute.Logging;

namespace UDRoute
{
    // ==========================================
    // 1. 核心启动与命令行/服务解析
    // ==========================================
    class Program
    {
        static async Task Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintHelp();
                return;
            }
            
            string cmd = args[0].ToLower();
            if (cmd == "-push" || cmd == "-pull")
            {
                await FileClientHelper.RunAsync(args);
                return;
            }
            else if (HandleServiceCommands(args)) return;

            bool isServiceMode = args.Any(a =>
                a.Equals("-service", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("-d", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("-daemon", StringComparison.OrdinalIgnoreCase));

            IsServiceMode = isServiceMode;

            var config = ConfigParser.Parse(args, isServiceMode);
            using var cts = new CancellationTokenSource();

            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

            var engine = new RouteEngine(config);
            try
            {
                await engine.StartAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // 忽略正常退出时的取消异常
            }
            catch (Exception ex)
            {
                Log.Error($"Fatal error: {ex.Message}");
            }
        }

        public static bool IsServiceMode;

        static bool HandleServiceCommands(string[] args)
        {
            string cmd = args[0].ToLower();

            if (cmd == "-h" || cmd == "-?" || cmd == "-help" || cmd == "--help")
            {
                PrintHelp();
                return true;
            }

            if (cmd == "-create" || cmd == "-init")
            {
                string path = args.Length > 1 && !args[1].StartsWith("-") ? args[1] : "udroute.ini";
                ConfigParser.CreateTemplate(path);
                return true;
            }

            string srvName = args.Length > 1 && !args[1].StartsWith("-") ? args[1] : "udroute";

            if (cmd == "-i")
            {
                InstallService(srvName);
                return true;
            }
            if (cmd == "-u")
            {
                UninstallService(srvName);
                return true;
            }
            if (cmd == "-status")
            {
                QueryStatus();
                return true;
            }
            if (cmd == "-hash")
            {
                if (args.Length > 1)
                {
                    string pass = args[1];
                    byte[] hash = ManagedSHA256.ComputeHashBytes(System.Text.Encoding.UTF8.GetBytes(pass));
                    Console.WriteLine("$HASH256$" + Convert.ToBase64String(hash));
                }
                else
                {
                    Console.Write("Enter Password: ");
                    string pass1 = ReadPassword();
                    Console.Write("Confirm Password: ");
                    string pass2 = ReadPassword();
                    if (pass1 != pass2)
                    {
                        Console.WriteLine("Passwords do not match.");
                    }
                    else if (string.IsNullOrEmpty(pass1))
                    {
                        Console.WriteLine("Password cannot be empty.");
                    }
                    else
                    {
                        byte[] hash = ManagedSHA256.ComputeHashBytes(System.Text.Encoding.UTF8.GetBytes(pass1));
                        Console.WriteLine("$HASH256$" + Convert.ToBase64String(hash));
                    }
                }
                return true;
            }

            return false;
        }

        static string ReadPassword()
        {
            string pass = "";
            while (true)
            {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (pass.Length > 0) pass = pass.Substring(0, pass.Length - 1);
                }
                else if (key.KeyChar != '\0')
                {
                    pass += key.KeyChar;
                }
            }
            Console.WriteLine();
            return pass;
        }

        static void PrintHelp()
        {
            try
            {
                bool isZh = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
                string resName = isZh ? "UDRoute.udroute_help_zh.txt" : "UDRoute.udroute_help_en.txt";

                using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(resName);
                if (stream != null)
                {
                    using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                    Console.WriteLine(reader.ReadToEnd());
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading help: {ex.Message}");
            }
        }

        static void QueryStatus()
        {
            var procs = Process.GetProcessesByName("udroute");
            bool found = false;

            foreach (var proc in procs)
            {
                if (proc.Id == Environment.ProcessId) continue;
                
                string pipeName = $"udroute_ctrl_{proc.Id}";
                try
                {
                    using var client = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.In);
                    client.Connect(500);

                    using var reader = new StreamReader(client, System.Text.Encoding.UTF8);
                    string statusText = reader.ReadToEnd();

                    if (!string.IsNullOrWhiteSpace(statusText))
                    {
                        found = true;
                        Console.WriteLine($"=== Status for udroute Process {proc.Id} ===");
                        Console.WriteLine(statusText);
                        Console.WriteLine("=====================================\n");
                    }
                }
                catch { } // Ignore processes that don't respond
            }

            if (!found)
            {
                Console.WriteLine("No running udroute processes found or they did not respond.");
            }
        }

        static void InstallService(string name)
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) exe = "udroute";
            var ini = name + ".ini";
            if (!File.Exists(ini)) ConfigParser.CreateTemplate(ini);
            
            string fullExe = Path.GetFullPath(exe);
            string fullIni = Path.GetFullPath(ini);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Windows sc 必须用外层引号包裹整个 binPath，然后内部路径如果带空格，需要用转义的双引号括起来
                string binPath = $"\\\"{fullExe}\\\" -service -c \\\"{fullIni}\\\"";
                Process.Start("sc", $"create {name} binPath= \"{binPath}\" start= auto").WaitForExit();
                Process.Start("sc", $"start {name}").WaitForExit();
                Console.WriteLine($"Windows Service {name} installed.");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                string svc = $"[Unit]\nDescription={name}\nAfter=network.target\n\n[Service]\nExecStart=\"{fullExe}\" -service -c \"{fullIni}\"\nRestart=always\nUser=root\n\n[Install]\nWantedBy=multi-user.target";
                File.WriteAllText($"/etc/systemd/system/{name.ToLower()}.service", svc);
                Process.Start("systemctl", "daemon-reload").WaitForExit();
                Process.Start("systemctl", $"enable {name.ToLower()}").WaitForExit();
                Process.Start("systemctl", $"start {name.ToLower()}").WaitForExit();
                Console.WriteLine($"Systemd Service {name} installed.");
            }
        }

        static void UninstallService(string name)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start("sc", $"stop {name}").WaitForExit();
                Process.Start("sc", $"delete {name}").WaitForExit();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("systemctl", $"stop {name.ToLower()}").WaitForExit();
                Process.Start("systemctl", $"disable {name.ToLower()}").WaitForExit();
                File.Delete($"/etc/systemd/system/{name.ToLower()}.service");
                Process.Start("systemctl", "daemon-reload").WaitForExit();
            }
            Console.WriteLine($"Service {name} uninstalled.");
        }
    }
}