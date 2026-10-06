using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using UDRoute;
using UDRoute.Maui.Data;
using UDRoute.Maui.Models;

namespace UDRoute.Maui.ViewModels;

public partial class SceneEditViewModel : ObservableObject
{
    private readonly LiteDbContext _db;
    private readonly int _sceneId;

    public event Action? RequestHideKeyboard;

    [ObservableProperty]
    private int _selectedTabIndex = 0; // 0: C, 1: S, 2: P

    [ObservableProperty]
    private string _sceneName = "新场境";

    // P 模式与全局设置
    [ObservableProperty]
    private bool _enableProxy = false;

    [ObservableProperty]
    private int _proxyPort = Constants.DefaultProxyPort;

    [ObservableProperty]
    private int _wanPort = 0;

    [ObservableProperty]
    private string _defaultServer = "";

    [ObservableProperty]
    private string _devName = "";

    [ObservableProperty]
    private int _regTimeout = Constants.DefaultRegTimeout;

    [ObservableProperty]
    private int _idleThreshold = Constants.DefaultIdleThreshold;

    [ObservableProperty]
    private int _probeTimeout = Constants.DefaultProbeTimeout;

    [ObservableProperty]
    private int _keepAlive = Constants.DefaultKeepAlive;

    [ObservableProperty]
    private int _maxRecentRequests = Constants.DefaultMaxRecentRequests;

    [ObservableProperty]
    private bool _forceRelay = false;

    // C 模式列表
    [ObservableProperty]
    private ObservableCollection<ClientItemViewModel> _clientItems = new();

    // S 模式列表
    [ObservableProperty]
    private ObservableCollection<ServerItemViewModel> _serverItems = new();

    // 帮助说明面板状态
    [ObservableProperty]
    private bool _isHelpVisible;

    [ObservableProperty]
    private string _helpTitle = "";

    [ObservableProperty]
    private string _helpContent = "";

    [ObservableProperty]
    private string _validationError = "";

    public SceneEditViewModel(LiteDbContext db, int sceneId = 0)
    {
        _db = db;
        _sceneId = sceneId;

        LoadSceneData();
    }

    private void LoadSceneData()
    {
        if (_sceneId > 0)
        {
            var scene = _db.Scenes.FindById(_sceneId);
            if (scene != null)
            {
                SceneName = scene.Name;
                var cfg = scene.Config;
                EnableProxy = cfg.EnableProxy;
                ProxyPort = cfg.Port > 0 ? cfg.Port : Constants.DefaultProxyPort;
                WanPort = cfg.WanPort;
                DefaultServer = cfg.Server ?? "";
                DevName = !string.IsNullOrEmpty(cfg.DevName) && !string.Equals(cfg.DevName, "localhost", StringComparison.OrdinalIgnoreCase) 
                    ? cfg.DevName 
                    : "";
                RegTimeout = cfg.RegTimeout;
                IdleThreshold = cfg.IdleThreshold;
                ProbeTimeout = cfg.ProbeTimeout;
                KeepAlive = cfg.KeepAlive;
                MaxRecentRequests = cfg.MaxRecentRequests;
                ForceRelay = cfg.ForceRelay;

                ClientItems.Clear();
                foreach (var c in cfg.ClientRecords)
                {
                    ClientItems.Add(new ClientItemViewModel(c, DefaultServer));
                }

                ServerItems.Clear();
                foreach (var s in cfg.ServerRecords)
                {
                    ServerItems.Add(new ServerItemViewModel(s, DefaultServer));
                }
                return;
            }
        }

        // 新建场境默认规则列表留空，不预置任何规则
        ClientItems.Clear();
        ServerItems.Clear();
    }

    partial void OnDefaultServerChanged(string value)
    {
        string placeholder = string.IsNullOrWhiteSpace(value) ? "未设置默认服务器" : value;
        foreach (var c in ClientItems)
        {
            c.PlaceholderServer = placeholder;
        }
        foreach (var s in ServerItems)
        {
            s.PlaceholderServer = placeholder;
        }
    }

    [RelayCommand]
    private void SwitchTab(string tabIndexStr)
    {
        if (int.TryParse(tabIndexStr, out int idx))
        {
            SelectedTabIndex = idx;
        }
    }

    [RelayCommand]
    private void AddClient()
    {
        ClientItems.Add(new ClientItemViewModel
        {
            Port = null,
            TargetName = "",
            PlaceholderServer = string.IsNullOrWhiteSpace(DefaultServer) ? "未设置默认服务器" : DefaultServer
        });
    }

    [RelayCommand]
    private void DeleteClient(ClientItemViewModel item)
    {
        ClientItems.Remove(item);
    }

    [RelayCommand]
    private void AddServer()
    {
        ServerItems.Add(new ServerItemViewModel
        {
            Name = "",
            TargetIp = "127.0.0.1",
            TargetPort = null,
            PlaceholderServer = string.IsNullOrWhiteSpace(DefaultServer) ? "未设置默认服务器" : DefaultServer
        });
    }

    [RelayCommand]
    private void DeleteServer(ServerItemViewModel item)
    {
        ServerItems.Remove(item);
    }

    [RelayCommand]
    public void ShowHelp(string param)
    {
        RequestHideKeyboard?.Invoke();

        var (title, desc) = GetHelpInfo(param);
        HelpTitle = title;
        HelpContent = desc;
        IsHelpVisible = true;
    }

    [RelayCommand]
    private void CloseHelp()
    {
        IsHelpVisible = false;
    }

    [RelayCommand]
    private async Task TestNatAsync(string? server)
    {
        string target = !string.IsNullOrWhiteSpace(server) ? server : DefaultServer;
        if (string.IsNullOrWhiteSpace(target))
        {
            if (Application.Current?.Windows.FirstOrDefault()?.Page != null)
            {
                await Application.Current.Windows[0].Page!.DisplayAlert("提示", "请先输入要测试的服务器地址 (如 pserver.example.com:9400)", "确定");
            }
            return;
        }

        var natVm = new NatDiagnosticViewModel(target);
        var natPage = new Views.NatDiagnosticPage(natVm);

        if (Shell.Current != null)
        {
            await Shell.Current.Navigation.PushAsync(natPage);
        }
        else if (Application.Current?.Windows.FirstOrDefault()?.Page?.Navigation != null)
        {
            await Application.Current.Windows[0].Page!.Navigation.PushAsync(natPage);
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ValidationError = "";

        // 校验场境名称
        if (string.IsNullOrWhiteSpace(SceneName))
        {
            ValidationError = "场境名称不能为空";
            if (Application.Current?.Windows.Count > 0 && Application.Current.Windows[0].Page != null)
                await Application.Current.Windows[0].Page!.DisplayAlert("提示", ValidationError, "确定");
            return;
        }

        // 校验 P 模式端口
        if (EnableProxy && (ProxyPort <= 0 || ProxyPort > 65535))
        {
            ValidationError = "P模式监听端口必须在 1 - 65535 范围内";
            if (Application.Current?.Windows.Count > 0 && Application.Current.Windows[0].Page != null)
                await Application.Current.Windows[0].Page!.DisplayAlert("提示", ValidationError, "确定");
            return;
        }

        // 校验 C 模式项目
        foreach (var c in ClientItems)
        {
            if (c.IsEnabled)
            {
                if (c.Port == null || c.Port <= 0 || c.Port > 65535)
                {
                    ValidationError = $"客户端本地端口不能为空且需在 1-65535 范围内";
                    if (Application.Current?.Windows.Count > 0 && Application.Current.Windows[0].Page != null)
                        await Application.Current.Windows[0].Page!.DisplayAlert("提示", ValidationError, "确定");
                    return;
                }
                if (string.IsNullOrWhiteSpace(c.TargetName))
                {
                    ValidationError = "客户端目标服务名称不能为空";
                    if (Application.Current?.Windows.Count > 0 && Application.Current.Windows[0].Page != null)
                        await Application.Current.Windows[0].Page!.DisplayAlert("提示", ValidationError, "确定");
                    return;
                }
            }
        }

        // 校验 S 模式项目
        foreach (var s in ServerItems)
        {
            if (s.IsEnabled)
            {
                if (string.IsNullOrWhiteSpace(s.Name))
                {
                    ValidationError = "服务端暴露服务名不能为空";
                    if (Application.Current?.Windows.Count > 0 && Application.Current.Windows[0].Page != null)
                        await Application.Current.Windows[0].Page!.DisplayAlert("提示", ValidationError, "确定");
                    return;
                }
                if (s.TargetPort == null || s.TargetPort <= 0 || s.TargetPort > 65535)
                {
                    ValidationError = $"服务端目标端口不能为空且需在 1-65535 范围内";
                    if (Application.Current?.Windows.Count > 0 && Application.Current.Windows[0].Page != null)
                        await Application.Current.Windows[0].Page!.DisplayAlert("提示", ValidationError, "确定");
                    return;
                }
                if (string.IsNullOrWhiteSpace(s.TargetIp))
                {
                    ValidationError = $"服务 [{s.Name}] 的目标IP不能为空";
                    if (Application.Current?.Windows.Count > 0 && Application.Current.Windows[0].Page != null)
                        await Application.Current.Windows[0].Page!.DisplayAlert("提示", ValidationError, "确定");
                    return;
                }
            }
        }

        // 构建保存对象
        var scene = _sceneId > 0 ? _db.Scenes.FindById(_sceneId) : new Scene();
        if (scene == null) scene = new Scene();

        scene.Name = SceneName;
        scene.Config.EnableProxy = EnableProxy;
        scene.Config.Port = ProxyPort;
        scene.Config.WanPort = WanPort;
        scene.Config.Server = DefaultServer;
        scene.Config.DevName = DevName;
        scene.Config.RegTimeout = RegTimeout;
        scene.Config.IdleThreshold = IdleThreshold;
        scene.Config.ProbeTimeout = ProbeTimeout;
        scene.Config.KeepAlive = KeepAlive;
        scene.Config.MaxRecentRequests = MaxRecentRequests;
        scene.Config.ForceRelay = ForceRelay;

        scene.Config.ClientRecords = ClientItems.Select(c => c.ToModel()).ToList();
        scene.Config.ServerRecords = ServerItems.Select(s => s.ToModel()).ToList();

        if (scene.Id > 0)
        {
            _db.Scenes.Update(scene);
        }
        else
        {
            _db.Scenes.Insert(scene);
        }

        if (Application.Current?.Windows.Count > 0 && Application.Current.Windows[0].Page != null)
            await Application.Current.Windows[0].Page!.DisplayAlert("成功", "场境配置已保存", "确定");
        await Shell.Current.GoToAsync("..");
    }

    private (string title, string desc) GetHelpInfo(string key) => key switch
    {
        "C_Port" => ("本地监听端口", "客户端在本地开启并监听的端口。访问此本地端口即可直接打通至远端对应的服务。"),
        "C_TargetName" => ("目标服务名称", "需要连接的远端服务端向中转锚点注册的服务唯一标识名称（例如 rdp, ssh, web 等）。"),
        "C_TargetServer" => ("目标中转服务器", "中转锚点(P)服务器的域名或公网IP地址与端口。\n若留空，将自动使用在【P：中转锚点】中设置的默认服务器。"),
        "C_ForceRelay" => ("强制中转", "开启后将跳过 P2P UDP 打洞直连探测，直接通过中转锚点转发全部流量，适用于对称型 NAT 严苛网络。"),
        "S_Name" => ("服务端标识名称", "当前设备向中转锚点注册的服务名称。客户端通过指定此名称与本端服务建立连接。"),
        "S_TargetIp" => ("目标局域网/本机IP", "服务端需要暴露并转发到的内部目标真实地址，例如 127.0.0.1 或局域网内其它设备的IP。"),
        "S_TargetPort" => ("目标服务真实端口", "内部目标服务正在监听的真实端口（例如 3389、80 等）。"),
        "S_TargetServer" => ("注册锚点服务器", "当前服务端向哪个中转锚点进行注册心跳。\n若留空，将自动使用在【P：中转锚点】中设置的默认服务器。"),
        "P_ProxyPort" => ("中转锚点监听端口", "作为 P 节点运行时，UDP 打洞注册以及流量中转监听的主端口，默认 9400。"),
        "P_DefaultServer" => ("默认锚点服务器", "当前场境的全局兜底中转服务器地址。\n当【C：客户端】或【S：服务端】未单独填写服务器时，将自动采用此处的配置。"),
        "General_MTU" => ("最大传输单元 (MTU)", "底层传输单个数据包的最大大小，默认 1400 字节，能适应绝大部分互联网 VPN 及移动蜂窝网络。"),
        "General_KeepAlive" => ("心跳保活间隔", "定期向对端或中转节点发送心跳包的间隔秒数，保持运营商 NAT 映射通道不被超时释放。"),
        _ => ("参数说明", "该参数用于控制 UDRoute 底层网络传输与路由行为。")
    };
}
