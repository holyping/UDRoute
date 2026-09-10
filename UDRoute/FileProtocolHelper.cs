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
    public static class FileProtocolHelper
    {
        public static async Task RunServerAsync(TcpListener listener, string baseDir, bool isReadOnly, CancellationToken ct)
        {
            try
            {
                using var reg = ct.Register(() => { try { listener.Stop(); } catch { } });
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var client = await listener.AcceptTcpClientAsync(ct);
                        _ = Task.Run(() => HandleClientAsync(client, baseDir, isReadOnly, ct), ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        if (ct.IsCancellationRequested) break;
                        Log.Debug($"[FileServer] Accept error: {ex.Message}");
                    }
                }
            }
            finally
            {
                try { listener.Stop(); } catch { }
            }
        }

        private static async Task HandleClientAsync(TcpClient client, string baseDir, bool isReadOnly, CancellationToken ct)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                try
                {
                    byte[] header = new byte[3];
                    if (!await ReadExactAsync(stream, header, ct)) return;

                    byte cmd = header[0];
                    int pathLen = header[1] | (header[2] << 8);

                    byte[] pathBuf = new byte[pathLen];
                    if (!await ReadExactAsync(stream, pathBuf, ct)) return;

                    string reqPath = Encoding.UTF8.GetString(pathBuf);
                    
                    // Normalize separators sent by client from potentially different OS
                    reqPath = reqPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

                    // Validate path
                    string fullBaseDir = Path.GetFullPath(baseDir);
                    if (!fullBaseDir.EndsWith(Path.DirectorySeparatorChar.ToString()))
                    {
                        fullBaseDir += Path.DirectorySeparatorChar;
                    }
                    
                    string fullPath = Path.GetFullPath(Path.Combine(baseDir, reqPath));
                    
                    // Allow exact match for base dir (if it's a directory query, though this is for files) or files inside
                    if (!fullPath.StartsWith(fullBaseDir, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Warn($"[FileServer] Blocked out-of-bound access: {reqPath}");
                        stream.WriteByte(0xFF);
                        return;
                    }

                    if (cmd == 0x01) // PUSH
                    {
                        if (isReadOnly)
                        {
                            Log.Warn($"[FileServer] PUSH rejected (ReadOnly mode): {reqPath}");
                            stream.WriteByte(0xFF);
                            await stream.FlushAsync(ct);
                            return;
                        }

                        // 检查目标文件是否已存在
                        if (File.Exists(fullPath))
                        {
                            // 告知客户端目标文件已存在，等待客户端确认覆盖 (0x01: 确认, 0x02: 取消)
                            stream.WriteByte(0x01);
                            await stream.FlushAsync(ct);

                            int confirm = stream.ReadByte();
                            if (confirm != 0x01)
                            {
                                Log.Info($"[FileServer] PUSH cancelled by client (file already exists): {fullPath}");
                                return;
                            }
                        }
                        else
                        {
                            // 目标文件不存在，直接就绪
                            stream.WriteByte(0x00);
                            await stream.FlushAsync(ct);
                        }

                        byte[] sizeBuf = new byte[8];
                        if (!await ReadExactAsync(stream, sizeBuf, ct)) return;
                        long fileSize = BitConverter.ToInt64(sizeBuf, 0);

                        string? dir = Path.GetDirectoryName(fullPath);
                        if (dir != null && !Directory.Exists(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }

                        using (var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            await CopyBytesAsync(stream, fs, fileSize, ct);
                        }
                        stream.WriteByte(0x00);
                        Log.Info($"[FileServer] Pushed file {fullPath} ({fileSize} bytes)");
                    }
                    else if (cmd == 0x02) // PULL
                    {
                        if (!File.Exists(fullPath))
                        {
                            Log.Warn($"[FileServer] PULL requested file not found: {fullPath}");
                            stream.WriteByte(0xFF);
                            return;
                        }

                        stream.WriteByte(0x00);
                        FileInfo fi = new FileInfo(fullPath);
                        long fileSize = fi.Length;
                        byte[] sizeBuf = BitConverter.GetBytes(fileSize);
                        await stream.WriteAsync(sizeBuf, 0, 8, ct);

                        using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            await CopyBytesAsync(fs, stream, fileSize, ct);
                        }
                        Log.Info($"[FileServer] Pulled file {fullPath} ({fileSize} bytes)");
                    }
                    else
                    {
                        Log.Warn($"[FileServer] Unknown command {cmd}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug($"[FileServer] Handle error: {ex.Message}");
                }
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

        private static async Task CopyBytesAsync(Stream src, Stream dst, long totalBytes, CancellationToken ct)
        {
            byte[] buf = new byte[81920];
            long copied = 0;
            while (copied < totalBytes)
            {
                int toRead = (int)Math.Min(buf.Length, totalBytes - copied);
                int read = await src.ReadAsync(buf, 0, toRead, ct);
                if (read == 0) throw new EndOfStreamException("Unexpected end of stream");
                await dst.WriteAsync(buf, 0, read, ct);
                copied += read;
            }
        }
    }
}

