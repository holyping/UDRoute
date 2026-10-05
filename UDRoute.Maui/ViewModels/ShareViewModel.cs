using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QRCoder;
using UDRoute.Maui.Models;
using UDRoute.Maui.Services;

namespace UDRoute.Maui.ViewModels;

public partial class ShareViewModel : ObservableObject
{
    [ObservableProperty]
    private string _sceneName = "";

    [ObservableProperty]
    private ImageSource? _qrImageSource;

    [ObservableProperty]
    private string _rawContent = "";

    [ObservableProperty]
    private string _summaryText = "";

    public ShareViewModel(Scene scene)
    {
        SceneName = scene.Name;
        RawContent = SceneTransferService.Export(scene);

        int clientCount = scene.Config.ClientRecords.Count;
        int serverCount = scene.Config.ServerRecords.Count;
        string proxyInfo = scene.Config.EnableProxy ? $"已启用P模式 (端口:{scene.Config.Port})" : "未启用P模式";
        SummaryText = $"包含 {clientCount} 条客户端规则，{serverCount} 个服务端服务 | {proxyInfo}";

        GenerateQrCode(RawContent);
    }

    private void GenerateQrCode(string payload)
    {
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
            var qrCode = new PngByteQRCode(data);
            byte[] qrBytes = qrCode.GetGraphic(20);

            QrImageSource = ImageSource.FromStream(() => new MemoryStream(qrBytes));
        }
        catch (Exception ex)
        {
            SummaryText = $"生成二维码失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CopyToClipboardAsync()
    {
        try
        {
            await Clipboard.Default.SetTextAsync(RawContent);
        }
        catch
        {
            // Ignore clipboard errors if any
        }

        // 复制后退出此显示页面
        if (Shell.Current != null)
        {
            await Shell.Current.GoToAsync("..");
        }
        else if (Application.Current?.Windows.FirstOrDefault()?.Page?.Navigation != null)
        {
            await Application.Current.Windows[0].Page!.Navigation.PopAsync();
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
