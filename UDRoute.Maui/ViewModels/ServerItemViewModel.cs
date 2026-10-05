using CommunityToolkit.Mvvm.ComponentModel;
using System.Text;
using UDRoute;

namespace UDRoute.Maui.ViewModels;

public partial class ServerItemViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _targetIp = "127.0.0.1";

    [ObservableProperty]
    private int _targetPort = 3389;

    [ObservableProperty]
    private bool _isTcp = true;

    [ObservableProperty]
    private string _targetServer = "";

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private int _regInterval = Constants.DefaultRegInterval;

    [ObservableProperty]
    private int _mtu = Constants.DefaultMtu;

    [ObservableProperty]
    private int _timeout = 0;

    [ObservableProperty]
    private int _tunnelReuseInterval = Constants.DefaultTunnelReuseInterval;

    [ObservableProperty]
    private bool _allowRelay = true;

    [ObservableProperty]
    private int _keepAlive = Constants.DefaultKeepAlive;

    // KCP properties
    [ObservableProperty]
    private bool _kcpNoDelay = true;

    [ObservableProperty]
    private int _kcpInterval = 10;

    [ObservableProperty]
    private int _kcpResend = 2;

    [ObservableProperty]
    private bool _kcpNc = true;

    [ObservableProperty]
    private int _kcpSndWnd = 128;

    [ObservableProperty]
    private int _kcpRcvWnd = 512;

    [ObservableProperty]
    private string _placeholderServer = "使用默认服务器";

    public ServerItemViewModel() { }

    public ServerItemViewModel(ServerRecord record, string defaultServer)
    {
        IsEnabled = record.IsEnabled;
        Name = record.Name;
        TargetIp = record.TargetIp;
        TargetPort = record.TargetPort;
        IsTcp = record.IsTcp;
        TargetServer = record.TargetServer;
        Password = record.Password != null ? Encoding.UTF8.GetString(record.Password) : "";
        RegInterval = record.RegInterval;
        Mtu = record.Mtu;
        Timeout = record.Timeout;
        TunnelReuseInterval = record.TunnelReuseInterval;
        AllowRelay = record.AllowRelay;
        KeepAlive = record.KeepAlive;

        if (record.KcpConfig != null)
        {
            KcpNoDelay = record.KcpConfig.NoDelay;
            KcpInterval = record.KcpConfig.Interval;
            KcpResend = record.KcpConfig.Resend;
            KcpNc = record.KcpConfig.Nc == 1;
            KcpSndWnd = record.KcpConfig.SndWnd;
            KcpRcvWnd = record.KcpConfig.RcvWnd;
        }

        PlaceholderServer = string.IsNullOrWhiteSpace(defaultServer) ? "未设置默认服务器" : defaultServer;
    }

    public ServerRecord ToModel()
    {
        var record = new ServerRecord
        {
            IsEnabled = IsEnabled,
            Name = Name,
            TargetIp = TargetIp,
            TargetPort = TargetPort,
            IsTcp = IsTcp,
            TargetServer = TargetServer,
            Password = !string.IsNullOrEmpty(Password) ? Encoding.UTF8.GetBytes(Password) : null,
            RegInterval = RegInterval,
            Mtu = Mtu,
            Timeout = Timeout,
            TunnelReuseInterval = TunnelReuseInterval,
            AllowRelay = AllowRelay,
            KeepAlive = KeepAlive
        };

        record.KcpConfig.NoDelay = KcpNoDelay;
        record.KcpConfig.Interval = KcpInterval;
        record.KcpConfig.Resend = KcpResend;
        record.KcpConfig.Nc = KcpNc ? 1 : 0;
        record.KcpConfig.SndWnd = KcpSndWnd;
        record.KcpConfig.RcvWnd = KcpRcvWnd;

        return record;
    }
}
