using UDRoute.Maui.ViewModels;

namespace UDRoute.Maui.Views;

public partial class SharePage : ContentPage
{
    public SharePage(ShareViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
