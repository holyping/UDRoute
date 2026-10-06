using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using UDRoute.Shared;
using UDRoute.Shared.Interfaces;
using UDRoute.Maui.Models;

namespace UDRoute.Maui.Services;

public partial class EngineService : ObservableObject, IEngineCallback
{
    private RouteEngine? _engine;
    private CancellationTokenSource? _cts;
    private Task? _runningTask;
    private readonly object _lock = new();

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _currentStatusText = "⚪ 已停止";

    [ObservableProperty]
    private string _currentPingText = "Ping: -- ms";

    public ObservableCollection<string> Logs { get; } = new();

    public async Task<bool> StartAsync(Scene scene)
    {
        lock (_lock)
        {
            if (IsRunning || IsBusy) return false;
            IsBusy = true;
        }

        CurrentStatusText = $"⏳ 正在启动: {scene.Name}...";
        UDRoute.Logging.Log.Info($"[Engine] 正在启动场境: {scene.Name}");

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _runningTask = Task.Run(async () =>
        {
            var config = scene.Config;
            try
            {
                _engine = new RouteEngine(config, this);
                var taskEngine = _engine.StartAsync(ct);
                
                // 标记初始绑定完成
                tcs.TrySetResult(true);

                var taskPoll = Task.Run(async () =>
                {
                    while (!ct.IsCancellationRequested)
                    {
                        try
                        {
                            var channels = _engine?.GetActiveChannels();
                            if (channels != null && channels.Count > 0)
                            {
                                var maxPing = channels.Max(c => c.Rtt);
                                OnPingUpdated(maxPing);
                            }
                            else
                            {
                                OnPingUpdated(0);
                            }
                        }
                        catch { }
                        await Task.Delay(2000, ct);
                    }
                }, ct);

                await Task.WhenAny(taskEngine, taskPoll);
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetResult(false);
            }
            catch (Exception ex)
            {
                UDRoute.Logging.Log.Error($"[Engine] 运行异常: {ex.Message}");
                tcs.TrySetException(ex);
            }
            finally
            {
                Cleanup();
                UDRoute.Logging.Log.Info($"[Engine] 场境已停止: {scene.Name}");
            }
        }, ct);

        try
        {
            var timeoutTask = Task.Delay(1500, ct);
            var finished = await Task.WhenAny(tcs.Task, timeoutTask);
            if (finished == tcs.Task)
            {
                await tcs.Task;
            }

            IsRunning = true;
            CurrentStatusText = $"🟢 运行中: {scene.Name}";
            return true;
        }
        catch (Exception ex)
        {
            UDRoute.Logging.Log.Error($"[Engine] 启动失败: {ex.Message}");
            CurrentStatusText = $"🔴 启动失败: {ex.Message}";
            await StopAsync();
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task StopAsync()
    {
        lock (_lock)
        {
            if (IsBusy) return;
            if (!IsRunning && _runningTask == null) return;
            IsBusy = true;
        }

        CurrentStatusText = "⏳ 正在停止场境...";
        UDRoute.Logging.Log.Info("[Engine] 正在停止场境...");

        try
        {
            await Task.Run(async () =>
            {
                try
                {
                    _cts?.Cancel();
                    _engine?.Dispose();

                    if (_runningTask != null)
                    {
                        await Task.WhenAny(_runningTask, Task.Delay(1000));
                    }
                }
                catch { }
            });
        }
        catch (Exception ex)
        {
            UDRoute.Logging.Log.Warn($"[Engine] 停止异常: {ex.Message}");
        }
        finally
        {
            Cleanup();
            IsBusy = false;
        }
    }

    public void Stop()
    {
        _ = StopAsync();
    }

    private void Cleanup()
    {
        try { _engine?.Dispose(); } catch { }
        _engine = null;
        try { _cts?.Dispose(); } catch { }
        _cts = null;
        _runningTask = null;
        IsRunning = false;
        CurrentStatusText = "⚪ 已停止";
        CurrentPingText = "Ping: -- ms";
    }

    public string GetStatusString()
    {
        return _engine?.GetAppStatusString() ?? "Engine is not running.";
    }

    public void OnLogMessage(int level, string message)
    {
        UDRoute.Logging.Log.Write((UDRoute.Logging.LogLevel)level, message);
    }

    public void OnStatusChanged(string status)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            CurrentStatusText = status;
        });
    }

    public void OnPingUpdated(int latencyMs)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            CurrentPingText = latencyMs > 0 ? $"Ping: {latencyMs} ms" : "Ping: -- ms";
        });
    }
}
