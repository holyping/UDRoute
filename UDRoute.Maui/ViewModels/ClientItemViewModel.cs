using CommunityToolkit.Mvvm.ComponentModel;
using System.Text;
using UDRoute;

namespace UDRoute.Maui.ViewModels;

public partial class ClientItemViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private int? _port = null;

    [ObservableProperty]
    private bool _isTcp = true;

    [ObservableProperty]
    private string _targetName = "";

    [ObservableProperty]
    private string _targetServer = "";

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private int _mtu = Constants.DefaultMtu;

    [ObservableProperty]
    private bool _forceRelay;

    [ObservableProperty]
    private int _timeout;

    [ObservableProperty]
    private int _keepAlive = Constants.DefaultKeepAlive;

    [ObservableProperty]
    private string _placeholderServer = "使用默认服务器";

    public ClientItemViewModel() { }

    public ClientItemViewModel(ClientRecord record, string defaultServer)
    {
        IsEnabled = record.IsEnabled;
        Port = record.Port > 0 ? record.Port : null;
        IsTcp = record.IsTcp;
        TargetName = record.TargetName;
        TargetServer = record.TargetServer;
        Password = record.Password != null ? Encoding.UTF8.GetString(record.Password) : "";
        Mtu = record.Mtu;
        ForceRelay = record.ForceRelay;
        Timeout = record.Timeout;
        KeepAlive = record.KeepAlive;
        PlaceholderServer = string.IsNullOrWhiteSpace(defaultServer) ? "未设置默认服务器" : defaultServer;
    }

    public ClientRecord ToModel()
    {
        return new ClientRecord
        {
            IsEnabled = IsEnabled,
            Port = Port ?? 0,
            IsTcp = IsTcp,
            TargetName = TargetName,
            TargetServer = TargetServer,
            Password = !string.IsNullOrEmpty(Password) ? Encoding.UTF8.GetBytes(Password) : null,
            Mtu = Mtu,
            ForceRelay = ForceRelay,
            Timeout = Timeout,
            KeepAlive = KeepAlive
        };
    }
}
