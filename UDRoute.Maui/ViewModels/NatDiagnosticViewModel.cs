using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.IO;
using UDRoute;

namespace UDRoute.Maui.ViewModels;

public partial class NatDiagnosticViewModel : ObservableObject
{
    [ObservableProperty]
    private string _serverAddress = "";

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private string _statusMessage = "准备就绪";

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private NatDiagnosticResult? _result;

    [ObservableProperty]
    private string _natLevelBadge = "检测中...";

    [ObservableProperty]
    private Color _natLevelColor = Colors.Gray;

    [ObservableProperty]
    private string _ipv4Summary = "--";

    [ObservableProperty]
    private string _ipv6Summary = "--";

    [ObservableProperty]
    private string _rawLogs = "";

    public NatDiagnosticViewModel(string serverAddress)
    {
        ServerAddress = serverAddress;
    }

    public async Task InitializeAsync()
    {
        await RunTestAsync();
    }

    [RelayCommand]
    public async Task RunTestAsync()
    {
        if (IsTesting) return;

        IsTesting = true;
        HasResult = false;
        StatusMessage = "正在探测目标服务器与 NAT 穿透模式...";
        RawLogs = "";

        try
        {
            using var writer = new StringWriter();
            var diagResult = await NatDiagnosticHelper.RunAsync(new[] { ServerAddress }, writer);
            Result = diagResult;
            RawLogs = writer.ToString();

            // 解析展示结果
            if (!string.IsNullOrEmpty(diagResult.NatLevel))
            {
                NatLevelBadge = diagResult.NatLevel;
                NatLevelColor = diagResult.IsConeNat == true ? Color.FromArgb("#10B981") : Color.FromArgb("#F59E0B");
            }
            else
            {
                NatLevelBadge = diagResult.IsConeNat == true ? "Cone NAT (可打洞穿透)" : "Symmetric NAT (中转优先)";
                NatLevelColor = diagResult.IsConeNat == true ? Color.FromArgb("#10B981") : Color.FromArgb("#EF4444");
            }

            Ipv4Summary = diagResult.IsIpv4Direct == true ? "公网 IPv4 直连" : (diagResult.IsIpv4Direct == false ? "NAT 路由转换" : "IPv4 不可用");
            Ipv6Summary = diagResult.IsIpv6Direct ? "支持公网 IPv6 直连" : "无有效 IPv6 地址";

            HasResult = true;
            StatusMessage = "诊断测试已完成";
        }
        catch (Exception ex)
        {
            StatusMessage = $"诊断过程发生异常: {ex.Message}";
            RawLogs += $"\n[!] 异常: {ex}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private async Task CloseAsync()
    {
        if (Shell.Current != null)
        {
            await Shell.Current.GoToAsync("..");
        }
        else if (Application.Current?.Windows.FirstOrDefault()?.Page?.Navigation != null)
        {
            await Application.Current.Windows[0].Page!.Navigation.PopAsync();
        }
    }
}
