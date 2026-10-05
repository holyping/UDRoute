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

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _currentStatusText = "⚪ 已停止";

    [ObservableProperty]
    private string _currentPingText = "Ping: -- ms";

    public ObservableCollection<string> Logs { get; } = new();

    public async Task StartAsync(Scene scene)
    {
        if (_isRunning) return;

        Logs.Clear();
        IsRunning = true;
        CurrentStatusText = $"🟢 运行中: {scene.Name}";

        var config = scene.Config;
        
        _cts = new CancellationTokenSource();
        _engine = new RouteEngine(config, this);

        try
        {
            var taskEngine = _engine.StartAsync(_cts.Token);
            var taskPoll = Task.Run(async () =>
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var channels = _engine.GetActiveChannels();
                        if (channels.Count > 0)
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
                    await Task.Delay(2000, _cts.Token);
                }
            }, _cts.Token);

            await Task.WhenAny(taskEngine, taskPoll);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            OnLogMessage((int)UDRoute.Logging.LogLevel.Error, $"Engine crashed: {ex.Message}");
        }
        finally
        {
            _engine?.Dispose();
            _engine = null;
            IsRunning = false;
            CurrentStatusText = "⚪ 已停止";
            CurrentPingText = "Ping: -- ms";
        }
    }

    public void Stop()
    {
        if (!_isRunning || _cts == null) return;
        _cts.Cancel();
    }

    public string GetStatusString()
    {
        return _engine?.GetAppStatusString() ?? "Engine is not running.";
    }

    public void OnLogMessage(int level, string message)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            Logs.Add($"[{time}] {message}");
            if (Logs.Count > 1000) Logs.RemoveAt(0);
        });
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
