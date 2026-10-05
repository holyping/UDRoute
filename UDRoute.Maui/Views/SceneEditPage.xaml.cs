using UDRoute.Maui.ViewModels;

namespace UDRoute.Maui.Views;

public partial class SceneEditPage : ContentPage
{
    public SceneEditPage(SceneEditViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;

        viewModel.RequestHideKeyboard += OnRequestHideKeyboard;
    }

    private void OnRequestHideKeyboard()
    {
        try
        {
#if ANDROID
            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (activity != null)
            {
                var view = activity.CurrentFocus;
                if (view != null)
                {
                    var imm = (Android.Views.InputMethods.InputMethodManager?)activity.GetSystemService(Android.Content.Context.InputMethodService);
                    imm?.HideSoftInputFromWindow(view.WindowToken, 0);
                }
            }
#elif IOS
            UIKit.UIApplication.SharedApplication.SendAction(
                new ObjCRuntime.Selector("resignFirstResponder"),
                null,
                null,
                null);
#endif
        }
        catch
        {
            // Fallback: ignore
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (BindingContext is SceneEditViewModel vm)
        {
            vm.RequestHideKeyboard -= OnRequestHideKeyboard;
        }
    }
}
