using System;
using Microsoft.Maui.Storage;
using UDRoute.Logging;

namespace UDRoute.Maui.Services
{
    public class MauiLogger : MemoryLogger
    {
        public const string LogLevelPreferenceKey = "UDRoute_Selected_LogLevel";

        public MauiLogger() : base(maxCapacity: 2000, defaultLevel: GetSavedLogLevel())
        {
        }

        public static LogLevel GetSavedLogLevel()
        {
            try
            {
                int saved = Preferences.Get(LogLevelPreferenceKey, (int)LogLevel.Info);
                if (Enum.IsDefined(typeof(LogLevel), saved))
                {
                    return (LogLevel)saved;
                }
            }
            catch { }
            return LogLevel.Info;
        }

        public void ChangeLogLevel(LogLevel newLevel)
        {
            if (Level == newLevel) return;

            Info($"日志级别即将切换为: {newLevel}");
            Level = newLevel;
            Log.Level = newLevel;
            try
            {
                Preferences.Set(LogLevelPreferenceKey, (int)newLevel);
            }
            catch { }
        }
    }
}
