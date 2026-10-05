using UDRoute.Maui.ViewModels;

namespace UDRoute.Maui.Views;

public partial class StatusPage : ContentPage
{
    private readonly StatusViewModel _viewModel;

    public StatusPage(StatusViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.OnAppearing();
    }
}
