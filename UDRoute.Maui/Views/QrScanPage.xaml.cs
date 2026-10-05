using UDRoute.Maui.Data;
using UDRoute.Maui.Services;
using ZXing.Net.Maui;

namespace UDRoute.Maui.Views;

public partial class QrScanPage : ContentPage
{
    private readonly LiteDbContext _db;
    private bool _isProcessing;

    public QrScanPage(LiteDbContext db)
    {
        InitializeComponent();
        _db = db;

        // 配置二维码识别选项
        BarcodeReader.Options = new BarcodeReaderOptions
        {
            Formats = BarcodeFormats.TwoDimensional,
            AutoRotate = true,
            Multiple = false
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
        {
            status = await Permissions.RequestAsync<Permissions.Camera>();
        }

        if (status != PermissionStatus.Granted)
        {
            await DisplayAlert("权限不足", "需要相机权限才能扫描二维码添加场境", "确定");
            await Navigation.PopAsync();
            return;
        }

        BarcodeReader.IsDetecting = true;
    }

    private void BarcodeReader_BarcodesDetected(object sender, BarcodeDetectionEventArgs e)
    {
        if (_isProcessing) return;

        var first = e.Results?.FirstOrDefault();
        if (first == null || string.IsNullOrWhiteSpace(first.Value)) return;

        _isProcessing = true;
        Dispatcher.Dispatch(async () =>
        {
            BarcodeReader.IsDetecting = false;

            var importedScene = SceneTransferService.Import(first.Value);
            if (importedScene != null)
            {
                importedScene.Name += " (扫码导入)";
                _db.Scenes.Insert(importedScene);

                await DisplayAlert("导入成功", $"已成功通过二维码导入场境：{importedScene.Name}", "确定");
                await Navigation.PopAsync();
            }
            else
            {
                bool retry = await DisplayAlert("识别失败", "未识别到有效的 UDRoute 场境配置，是否重试？", "重试", "取消");
                if (retry)
                {
                    _isProcessing = false;
                    BarcodeReader.IsDetecting = true;
                }
                else
                {
                    await Navigation.PopAsync();
                }
            }
        });
    }

    private async void Close_Clicked(object sender, EventArgs e)
    {
        await Navigation.PopAsync();
    }
}
