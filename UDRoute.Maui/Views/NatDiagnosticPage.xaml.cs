using UDRoute.Maui.ViewModels;

namespace UDRoute.Maui.Views;

public partial class NatDiagnosticPage : ContentPage
{
    private bool _hasStarted;

    public NatDiagnosticPage(NatDiagnosticViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!_hasStarted && BindingContext is NatDiagnosticViewModel vm)
        {
            _hasStarted = true;
            await vm.InitializeAsync();
        }
    }
}
