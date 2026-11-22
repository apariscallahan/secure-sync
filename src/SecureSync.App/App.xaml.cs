using System.Windows;
using System.Windows.Threading;

namespace SecureSync.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        if (e.Args is ["--self-test", var reportPath])
        {
            Shutdown(SelfTest.Run(reportPath));
            return;
        }

#if DEBUG
        // Renders each screen to PNG for visual checks during development: SecureSync.exe --screenshots <dir>
        if (e.Args is ["--screenshots", var outputDir])
        {
            ErrorLogPath = System.IO.Path.Combine(outputDir, "unhandled.txt");
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dev.Screenshots.Render(outputDir); // shuts the app down when finished
            return;
        }
        if (e.Args is ["--make-icon", var iconPath])
        {
            Dev.IconMaker.Write(iconPath);
            Shutdown();
            return;
        }
#endif

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>When set (screenshot runs), unhandled errors are written here instead of shown in a message box.</summary>
    internal static string? ErrorLogPath { get; private set; }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (ErrorLogPath is not null)
        {
            System.IO.File.AppendAllText(ErrorLogPath, e.Exception + Environment.NewLine);
            e.Handled = true;
            return;
        }
        MessageBox.Show(MainWindow, $"Something went wrong:\n\n{e.Exception.Message}", "SecureSync",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
