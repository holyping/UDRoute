using Microsoft.Maui.Controls;
using UDRoute.Maui.ViewModels;

namespace UDRoute.Maui.Views;

public partial class LogPage : ContentPage
{
    private readonly LogViewModel _vm;

    public LogPage(LogViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = viewModel;

        _vm.RequestScrollToBottom += OnRequestScrollToBottom;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.OnPageAppearingAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.OnPageDisappearing();
    }

    private void OnRequestScrollToBottom()
    {
        if (_vm.Logs.Count > 0)
        {
            int index = _vm.Logs.Count - 1;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    LogCollectionView.ScrollTo(index, position: ScrollToPosition.End, animate: false);
                }
                catch { }
            });
        }
    }
}
