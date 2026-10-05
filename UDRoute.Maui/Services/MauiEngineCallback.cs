using UDRoute.Shared.Interfaces;

namespace UDRoute.Maui.Services;

public class MauiEngineCallback : IEngineCallback
{
    public event Action<int, string>? LogMessageReceived;
    public event Action<string>? StatusChanged;
    public event Action<int>? PingUpdated;

    public void OnLogMessage(int level, string message)
    {
        LogMessageReceived?.Invoke(level, message);
    }

    public void OnStatusChanged(string status)
    {
        StatusChanged?.Invoke(status);
    }

    public void OnPingUpdated(int latencyMs)
    {
        PingUpdated?.Invoke(latencyMs);
    }
}
