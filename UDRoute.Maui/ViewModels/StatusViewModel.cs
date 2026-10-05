using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UDRoute.Maui.Services;

namespace UDRoute.Maui.ViewModels;

public partial class StatusViewModel : ObservableObject
{
    private readonly EngineService _engine;

    [ObservableProperty]
    private string _statusText = "Engine is not running.";

    public StatusViewModel(EngineService engine)
    {
        _engine = engine;
    }

    [RelayCommand]
    private void Refresh()
    {
        StatusText = _engine.GetStatusString();
    }

    public void OnAppearing()
    {
        Refresh();
    }
}
