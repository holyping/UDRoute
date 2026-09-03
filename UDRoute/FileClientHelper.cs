using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UDRoute.Logging;

namespace UDRoute
{
    public static class FileClientHelper
    {
        public static async Task RunAsync(string[] args)
        {
            if (args.Length < 4)
            {
                Console.WriteLine("Usage: udroute -push/-pull name@PAddress SFilePath LocalFilePath/con:");
                return;
            }

            bool isPush = args[0].ToLower() == "-push";
            string targetArg = args[1]; // name@PAddress
            string sFilePath = args[2];
            string localFilePath = args[3];

            bool isConsole = localFilePath.Equals("con:", StringComparison.OrdinalIgnoreCase);

            var valParts = targetArg.Split('@');
            string namePart = valParts[0];
            string targetServer = valParts.Length > 1 ? valParts[1] : "localhost";

            string targetName = namePart;
            byte[]? passwordHash = null;
            int colonIdx = namePart.IndexOf(':');
            if (colonIdx > 0)
            {
                targetName = namePart.Substring(0, colonIdx);
                string passPart = namePart.Substring(colonIdx + 1);
                if (passPart.StartsWith("$HASH256$"))
                {
                    passwordHash = Convert.FromBase64String(passPart.Substring(9));
                }
                else
                {
                    passwordHash = ManagedSHA256.ComputeHashBytes(System.Text.Encoding.UTF8.GetBytes(passPart));
                }
            }

            // Find free port
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int localPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            var config = new AppConfig();
            config.ClientRecords.Add(new ClientRecord
            {
                Port = localPort,
                TargetName = targetName.EndsWith("/file", StringComparison.OrdinalIgnoreCase) ? targetName : targetName + "/file",
                TargetServer = targetServer,
                IsTcp = true,
                Password = passwordHash,
                IsThis = string.IsNullOrEmpty(targetServer) || targetServer.Equals("this", StringComparison.OrdinalIgnoreCase)
            });
            if (config.ClientRecords[0].IsThis) config.EnableProxy = true;
            if (isConsole)
            {
                config.LogLevel = LogLevel.None;
            }
            else
            {
                config.LogLevel = LogLevel.Error;
            }
            Log.Init(config, false);

            if (isConsole)
            {
                Log.SetLogger(new NullLogger());
            }

            using var cts = new CancellationTokenSource();
            using var udp = new ZeroCopyUdpSocket(0);
            ProxyMode? proxy = null;
            if (config.EnableProxy)
            {
                proxy = new ProxyMode(config, udp);
                _ = proxy.RunAsync(cts.Token);
            }

            var clientMode = new ClientMode(config, udp, proxy);
            var runTask = clientMode.RunAsync(cts.Token);

            // Wait a little bit for ClientMode to start listening
            await Task.Delay(500);

            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync("127.0.0.1", localPort);
                using var stream = tcp.GetStream();

                byte[] pathBytes = Encoding.UTF8.GetBytes(sFilePath);
                byte[] header = new byte[3];
                header[0] = isPush ? (byte)0x01 : (byte)0x02;
                header[1] = (byte)(pathBytes.Length & 0xFF);
                header[2] = (byte)((pathBytes.Length >> 8) & 0xFF);

                await stream.WriteAsync(header, 0, 3);
                await stream.WriteAsync(pathBytes, 0, pathBytes.Length);

                if (isPush)
                {
                    long fileSize = 0;
                    Stream srcStream;
                    if (isConsole)
                    {
                        var mem = new MemoryStream();
                        using (var stdin = Console.OpenStandardInput())
                        {
                            await stdin.CopyToAsync(mem);
                        }
                        mem.Position = 0;
                        fileSize = mem.Length;
                        srcStream = mem;
                    }
                    else
                    {
                        FileInfo fi = new FileInfo(localFilePath);
                        fileSize = fi.Length;
                        srcStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    }

                    byte[] sizeBuf = BitConverter.GetBytes(fileSize);
                    await stream.WriteAsync(sizeBuf, 0, 8);

                    using (srcStream)
                    {
                        await CopyWithProgressAsync(srcStream, stream, fileSize, isConsole, cts.Token);
                    }

                    int res = stream.ReadByte();
                    if (res == 0) Console.WriteLine("\nPush successful.");
                    else Console.WriteLine("\nPush failed on server.");
                }
                else
                {
                    int res = stream.ReadByte();
                    if (res != 0)
                    {
                        Console.WriteLine("Pull failed on server. File might not exist or access denied.");
                        return;
                    }

                    byte[] sizeBuf = new byte[8];
                    await ReadExactAsync(stream, sizeBuf, cts.Token);
                    long fileSize = BitConverter.ToInt64(sizeBuf, 0);

                    Stream dstStream;
                    if (isConsole)
                    {
                        dstStream = Console.OpenStandardOutput();
                    }
                    else
                    {
                        string dir = Path.GetDirectoryName(localFilePath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                        dstStream = new FileStream(localFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
                    }

                    using (dstStream)
                    {
                        await CopyWithProgressAsync(stream, dstStream, fileSize, isConsole, cts.Token);
                    }
                    if (!isConsole) Console.WriteLine("\nPull successful.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nError: {ex.Message}");
            }
            finally
            {
                cts.Cancel();
            }
        }

        private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, ct);
                if (read == 0) return false;
                offset += read;
            }
            return true;
        }

        private static async Task CopyWithProgressAsync(Stream src, Stream dst, long totalBytes, bool isConsole, CancellationToken ct)
        {
            byte[] buf = new byte[81920];
            long copied = 0;
            long lastReport = Environment.TickCount64;
            while (copied < totalBytes)
            {
                int toRead = (int)Math.Min(buf.Length, totalBytes - copied);
                int read = await src.ReadAsync(buf, 0, toRead, ct);
                if (read == 0) throw new EndOfStreamException("Unexpected end of stream");
                await dst.WriteAsync(buf, 0, read, ct);
                copied += read;

                if (!isConsole && Environment.TickCount64 - lastReport > 200)
                {
                    lastReport = Environment.TickCount64;
                    double percent = (double)copied / totalBytes * 100;
                    Console.Write($"\r[{"".PadRight((int)(percent/2), '#')}{"".PadRight(50 - (int)(percent/2), ' ')}] {percent:F1}% {copied}/{totalBytes} bytes");
                }
            }
            if (!isConsole)
            {
                Console.Write($"\r[{"".PadRight(50, '#')}] 100.0% {totalBytes}/{totalBytes} bytes\n");
            }
        }
    }
}
