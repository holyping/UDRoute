using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UDRoute
{
    public static class RemoteControlHelper
    {
        public static async Task RunAsync(string[] args)
        {
            if (args.Length < 2)
            {
                PrintUsage();
                Environment.ExitCode = 1;
                return;
            }

            string cmd = args[0].ToLower();
            ControlAction action;
            if (cmd == "-add")
            {
                action = ControlAction.Add;
            }
            else if (cmd == "-delete" || cmd == "-del" || cmd == "-rm" || cmd == "-remove")
            {
                action = ControlAction.Delete;
            }
            else if (cmd == "-list" || cmd == "-ls")
            {
                action = ControlAction.List;
            }
            else
            {
                PrintUsage();
                Environment.ExitCode = 1;
                return;
            }

            string? explicitPassword = null;
            var positional = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if ((arg.Equals("-pwd", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("-p", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("--password", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    explicitPassword = args[++i];
                }
                else
                {
                    positional.Add(arg);
                }
            }

            if (action == ControlAction.List)
            {
                if (positional.Count < 2)
                {
                    Console.WriteLine(I18n.Text("用法: udroute -list <主机[:端口][/密码]>", "Usage: udroute -list <Host[:Port][/Password]>"));
                    Environment.ExitCode = 1;
                    return;
                }
            }
            else
            {
                if (positional.Count < 3)
                {
                    string actionName = action == ControlAction.Add ? "-add" : "-delete";
                    string argPrompt = action == ControlAction.Add ? "<端点定义1> [端点定义2]..." : "<端口/服务名1> [端口/服务名2]...";
                    string argPromptEn = action == ControlAction.Add ? "<EndpointDef1> [EndpointDef2]..." : "<Port|Name1> [Port|Name2]...";
                    Console.WriteLine(I18n.Text($"用法: udroute {actionName} <主机[:端口][/密码]> {argPrompt}", $"Usage: udroute {actionName} <Host[:Port][/Password]> {argPromptEn}"));
                    Environment.ExitCode = 1;
                    return;
                }
            }

            string targetSpec = positional[1];
            var (host, port, password) = ParseTargetSpec(targetSpec, explicitPassword);

            if (password == null)
            {
                password = PromptPasswordMasked();
            }

            var payloads = new List<string>();
            if (action == ControlAction.Add)
            {
                for (int i = 2; i < positional.Count; i++)
                {
                    string item = positional[i].Trim();
                    int eqIdx = item.IndexOf('=');
                    if (eqIdx <= 0)
                    {
                        Console.WriteLine(I18n.Text(
                            $"[!] 错误: 端点定义格式无效 '{item}'。应包含 '='，例如 3443=xeno@www.qzsoft.top 或 web=127.0.0.1:80/tcp@www.qzsoft.top。",
                            $"[!] Error: Invalid endpoint definition format '{item}'. Expected '=' in definition, e.g. 3443=xeno@www.qzsoft.top."));
                        Environment.ExitCode = 1;
                        return;
                    }
                    payloads.Add(item);
                }
            }
            else if (action == ControlAction.Delete)
            {
                for (int i = 2; i < positional.Count; i++)
                {
                    string item = positional[i].Trim();
                    int eqIdx = item.IndexOf('=');
                    if (eqIdx > 0)
                    {
                        item = item.Substring(0, eqIdx).Trim();
                    }
                    if (!string.IsNullOrWhiteSpace(item))
                    {
                        payloads.Add(item);
                    }
                }
            }

            Console.WriteLine(I18n.Text($"[Control] 正在连接远程 udroute ({host}:{port}) 发送控制指令...", $"[Control] Connecting to remote udroute ({host}:{port}) to send command..."));

            var (success, message) = await SendControlCommandAsync(host, port, action, payloads, password);

            if (action == ControlAction.List)
            {
                if (success)
                {
                    Console.WriteLine(I18n.Text($"\n=== 远程 udroute ({host}:{port}) 端点列表 ===", $"\n=== Endpoints on Remote udroute ({host}:{port}) ==="));
                    Console.WriteLine(message);
                    Console.WriteLine("=====================================\n");
                    Environment.ExitCode = 0;
                }
                else
                {
                    Console.WriteLine(I18n.Text($"[!] 查询远程端点失败: {message}", $"[!] Failed to list remote endpoints: {message}"));
                    Environment.ExitCode = 1;
                }
            }
            else if (action == ControlAction.Add)
            {
                if (success)
                {
                    Console.WriteLine(I18n.Text($"[+] 成功添加端点:\n{message}", $"[+] Successfully added endpoint(s):\n{message}"));
                    Environment.ExitCode = 0;
                }
                else
                {
                    Console.WriteLine(I18n.Text($"[!] 添加端点失败:\n{message}", $"[!] Failed to add endpoint(s):\n{message}"));
                    Environment.ExitCode = 1;
                }
            }
            else // Delete
            {
                if (success)
                {
                    Console.WriteLine(I18n.Text($"[+] 成功删除端点:\n{message}", $"[+] Successfully deleted endpoint(s):\n{message}"));
                    Environment.ExitCode = 0;
                }
                else
                {
                    Console.WriteLine(I18n.Text($"[!] 删除端点失败:\n{message}", $"[!] Failed to delete endpoint(s):\n{message}"));
                    Environment.ExitCode = 1;
                }
            }
        }

        public static (string Host, int Port, string? Password) ParseTargetSpec(string targetSpec, string? fallbackPassword = null)
        {
            string hostPortPart;
            string? pwd = fallbackPassword;

            int slashIdx = targetSpec.IndexOf('/');
            if (slashIdx >= 0)
            {
                hostPortPart = targetSpec.Substring(0, slashIdx).Trim();
                pwd = targetSpec.Substring(slashIdx + 1);
            }
            else
            {
                hostPortPart = targetSpec.Trim();
            }

            var (host, port) = ProtocolHelper.ParseHostAndPort(hostPortPart, Constants.DefaultControllerPort);
            return (host, port, pwd);
        }

        public static string PromptPasswordMasked()
        {
            Console.Write(I18n.Text("请输入远程控制密码 (ControllerPassword): ", "Enter remote controller password (ControllerPassword): "));
            if (Console.IsInputRedirected)
            {
                return Console.ReadLine() ?? "";
            }

            var sb = new StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    break;
                }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (sb.Length > 0)
                    {
                        sb.Length--;
                    }
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    sb.Append(key.KeyChar);
                }
            }
            return sb.ToString();
        }

        public static async Task<(bool Success, string Message)> SendControlCommandAsync(
            string host,
            int port,
            ControlAction action,
            List<string> payloads,
            string? password,
            int timeoutMs = 5000,
            CancellationToken ct = default)
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
            var token = linkedCts.Token;

            try
            {
                await tcp.ConnectAsync(host, port, token);
            }
            catch (Exception ex)
            {
                return (false, I18n.Text(
                    $"无法连接至目标主机 ({host}:{port}) TCP 控制端口: {ex.Message}。请确认远程 udroute 正在运行且已配置 ControllerPassword 启用远程控制。",
                    $"Failed to connect to target ({host}:{port}) TCP controller port: {ex.Message}. Verify remote udroute is running and ControllerPassword is configured."));
            }

            try
            {
                using var stream = tcp.GetStream();

                var requestId = Guid.NewGuid();
                long timestamp = DateTime.UtcNow.Ticks;

                // Calculate authHash = SHA256(SHA256(password) + timestamp)
                byte[] pwdHash = ManagedSHA256.ComputeHashBytes(Encoding.UTF8.GetBytes(password ?? ""));
                byte[] hashInput = new byte[32 + 8];
                pwdHash.CopyTo(hashInput, 0);
                BinaryPrimitives.WriteInt64LittleEndian(hashInput.AsSpan(32, 8), timestamp);
                byte[] authHash = ManagedSHA256.ComputeHashBytes(hashInput);

                // Calculate payload byte sizes
                int payloadsBytes = 0;
                foreach (var p in payloads)
                {
                    payloadsBytes += 4 + Encoding.UTF8.GetByteCount(p);
                }

                int bodyLen = 1 + 16 + 1 + 8 + 32 + 4 + payloadsBytes;
                byte[] reqBuf = new byte[4 + bodyLen];

                BinaryPrimitives.WriteInt32LittleEndian(reqBuf.AsSpan(0, 4), bodyLen);
                reqBuf[4] = (byte)MsgType.ControlReq;
                requestId.TryWriteBytes(reqBuf.AsSpan(5, 16));
                reqBuf[21] = (byte)action;
                BinaryPrimitives.WriteInt64LittleEndian(reqBuf.AsSpan(22, 8), timestamp);
                authHash.CopyTo(reqBuf.AsSpan(30, 32));
                BinaryPrimitives.WriteInt32LittleEndian(reqBuf.AsSpan(62, 4), payloads.Count);

                int offset = 66;
                foreach (var p in payloads)
                {
                    offset += ProtocolHelper.WriteString(reqBuf.AsSpan(offset), p);
                }

                await stream.WriteAsync(reqBuf, token);
                await stream.FlushAsync(token);

                // Read response length
                byte[] lenBuf = new byte[4];
                await stream.ReadExactlyAsync(lenBuf, token);
                int respLen = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
                if (respLen < 19 || respLen > 1024 * 1024)
                {
                    return (false, "Invalid response packet length from remote udroute.");
                }

                byte[] respBuf = new byte[respLen];
                await stream.ReadExactlyAsync(respBuf, token);

                if ((MsgType)respBuf[0] != MsgType.ControlResp)
                {
                    return (false, "Unexpected response message type from remote udroute.");
                }

                bool status = respBuf[18] == 1;
                string message = "";
                if (respLen > 19)
                {
                    (message, _) = ProtocolHelper.ReadString(respBuf.AsSpan(19, respLen - 19));
                }

                return (status, message);
            }
            catch (OperationCanceledException)
            {
                return (false, I18n.Text($"与远程 udroute ({host}:{port}) 通信超时。", $"Timed out communicating with remote udroute ({host}:{port})."));
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine(I18n.Text(
                "用法 (Usage):\n" +
                "  udroute -add <主机[:端口][/密码]> <端点定义1> [端点定义2]...\n" +
                "  udroute -delete <主机[:端口][/密码]> <端口/服务名1> [端口/服务名2]...\n" +
                "  udroute -list <主机[:端口][/密码]>\n\n" +
                "说明:\n" +
                "  - 远程端口未指定时，默认使用独立 TCP 控制端口 9401。\n" +
                "  - 密码可以写在地址后 (如 192.168.1.4/mypassword)，也可以省略，程序会在控制台安全提示输入 (无回显)。\n" +
                "  - 远程 udroute 节点必须在配置文件中设置 ControllerPassword 才会开启 9401 控制端口。\n\n" +
                "示例 (Examples):\n" +
                "  udroute -add 192.168.1.4/mypassword 3443=xeno@www.qzsoft.top\n" +
                "  udroute -add 192.168.1.4/mypassword 3443=xeno@www.qzsoft.top 11433=sql@www.qzsoft.top\n" +
                "  udroute -delete 192.168.1.4/mypassword 3443\n" +
                "  udroute -del 192.168.1.4/mypassword 3443 11433\n" +
                "  udroute -list 192.168.1.4/mypassword\n" +
                "  udroute -list 192.168.1.4",
                "Usage:\n" +
                "  udroute -add <Host[:Port][/Password]> <EndpointDef1> [EndpointDef2]...\n" +
                "  udroute -delete <Host[:Port][/Password]> <Port|Name1> [Port|Name2]...\n" +
                "  udroute -list <Host[:Port][/Password]>\n\n" +
                "Notes:\n" +
                "  - Default TCP controller port is 9401 if port is omitted.\n" +
                "  - Password can be embedded (e.g. 192.168.1.4/mypassword) or omitted to trigger masked interactive prompt.\n" +
                "  - Remote udroute must have ControllerPassword set in ini to enable TCP 9401 remote control.\n\n" +
                "Examples:\n" +
                "  udroute -add 192.168.1.4/mypassword 3443=xeno@www.qzsoft.top\n" +
                "  udroute -add 192.168.1.4/mypassword 3443=xeno@www.qzsoft.top 11433=sql@www.qzsoft.top\n" +
                "  udroute -delete 192.168.1.4/mypassword 3443\n" +
                "  udroute -del 192.168.1.4/mypassword 3443 11433\n" +
                "  udroute -list 192.168.1.4/mypassword\n" +
                "  udroute -list 192.168.1.4"
            ));
        }
    }
}
