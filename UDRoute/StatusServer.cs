using System;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UDRoute.Logging;

namespace UDRoute
{
    public static class StatusServer
    {
        public static async Task StartAsync(RouteEngine engine, CancellationToken ct)
        {
            string pipeName = $"udroute_ctrl_{Environment.ProcessId}";
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var pipeServer = new NamedPipeServerStream(
                        pipeName, 
                        PipeDirection.Out, 
                        NamedPipeServerStream.MaxAllowedServerInstances, 
                        PipeTransmissionMode.Byte, 
                        PipeOptions.Asynchronous);
                    
                    await pipeServer.WaitForConnectionAsync(ct);

                    string statusStr = engine.GetAppStatusString();
                    var bytes = Encoding.UTF8.GetBytes(statusStr);
                    
                    await pipeServer.WriteAsync(bytes, ct);
                    pipeServer.Disconnect();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log.Error($"[IPC] Status server error: {ex.Message}");
                    await Task.Delay(1000, ct);
                }
            }
        }
    }
}
