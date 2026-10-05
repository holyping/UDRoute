using UDRoute.Maui.ViewModels;

namespace UDRoute.Maui.Views;

public partial class LogPage : ContentPage
{
    public LogPage(LogViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
