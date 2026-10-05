namespace UDRoute.Shared.Interfaces;

public interface IEngineCallback
{
    void OnLogMessage(int level, string message);
    void OnStatusChanged(string status);
    void OnPingUpdated(int latencyMs);
}
