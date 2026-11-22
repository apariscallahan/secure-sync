namespace SecureSync.Mobile;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        UserAppTheme = AppTheme.Light;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new NavigationPage(new LockPage()));
        window.Stopped += (_, _) => AppState.Current.OnBackground();
        window.Resumed += async (_, _) => await AppState.Current.OnForegroundAsync();
        return window;
    }
}
