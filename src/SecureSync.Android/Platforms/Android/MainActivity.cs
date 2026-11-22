using Android.App;
using Android.Content.PM;
using Android.Views;

namespace SecureSync.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout
                           | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Android.OS.Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // Dark status-bar icons on the app's light background.
        if (Window is { } window && AndroidX.Core.View.WindowCompat.GetInsetsController(window, window.DecorView) is { } insets)
            insets.AppearanceLightStatusBars = true;
#if !ALLOW_SCREENSHOTS // test builds only (build with -p:AllowScreenshots=true)
        // Keep file names and the chain key out of the recent-apps thumbnail and screenshots.
        Window?.SetFlags(WindowManagerFlags.Secure, WindowManagerFlags.Secure);
#endif
    }
}
