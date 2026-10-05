using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;
using ZXing.Net.Maui.Controls;
using UDRoute.Maui.Data;
using UDRoute.Maui.ViewModels;
using UDRoute.Maui.Views;

namespace UDRoute.Maui;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseMauiCommunityToolkit()
			.UseBarcodeReader()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

        // 注册数据库上下文 (存储于 AppData 目录)
        string dbPath = Path.Combine(FileSystem.AppDataDirectory, "udroute.db");
        builder.Services.AddSingleton(new LiteDbContext(dbPath));

        // 注册页面与 ViewModels
        builder.Services.AddSingleton<UDRoute.Maui.Services.EngineService>();

        builder.Services.AddSingleton<HomeViewModel>();
        builder.Services.AddSingleton<HomePage>();

        builder.Services.AddSingleton<StatusViewModel>();
        builder.Services.AddSingleton<StatusPage>();

        builder.Services.AddSingleton<LogViewModel>();
        builder.Services.AddSingleton<LogPage>();

        builder.Services.AddTransient<SceneEditViewModel>();
        builder.Services.AddTransient<SceneEditPage>();

        builder.Services.AddTransient<ShareViewModel>();
        builder.Services.AddTransient<SharePage>();

        builder.Services.AddTransient<NatDiagnosticViewModel>();
        builder.Services.AddTransient<NatDiagnosticPage>();

        builder.Services.AddTransient<QrScanPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
