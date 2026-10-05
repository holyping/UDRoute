using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using UDRoute.Maui.Services;

namespace UDRoute.Maui.ViewModels;

public partial class LogViewModel : ObservableObject
{
    public EngineService Engine { get; }

    public ObservableCollection<string> Logs => Engine.Logs;

    private readonly UDRoute.Maui.Data.LiteDbContext _db;

    public LogViewModel(EngineService engine, UDRoute.Maui.Data.LiteDbContext db)
    {
        Engine = engine;
        _db = db;
    }

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (!Engine.IsRunning)
        {
            var selected = _db.Scenes.FindOne(s => s.IsSelected) ?? _db.Scenes.FindAll().FirstOrDefault();
            if (selected != null)
            {
                await Engine.StartAsync(selected);
            }
            else
            {
                Engine.OnLogMessage((int)UDRoute.Logging.LogLevel.Warn, "未找到任何场境可启动。");
            }
        }
        else
        {
            Engine.Stop();
        }
    }
}
