using System.Net;
using System.Windows;
using SecureSync.Core.Sync;

namespace SecureSync.App.Views;

public partial class ConnectDialog : Window
{
    private const int DefaultPort = 47821;
    private readonly SyncEngine _engine;

    public ConnectDialog(SyncEngine engine)
    {
        InitializeComponent();
        _engine = engine;
        ThisComputerRun.Text = string.Join(", ", ExplorerView.LocalAddresses().Select(a => $"{a}:{engine.ListenPort}"));
        Loaded += (_, _) => AddressBox.Focus();
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        ErrorBox.Visibility = Visibility.Collapsed;
        if (!IPEndPoint.TryParse(AddressBox.Text.Trim(), out var endpoint) || endpoint.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            ShowError("Enter an IPv4 address like 192.168.1.20 (optionally followed by :port).");
            return;
        }
        if (endpoint.Port == 0) endpoint.Port = DefaultPort;

        ConnectButton.IsEnabled = false;
        Spinner.Visibility = Visibility.Visible;
        try
        {
            await _engine.ConnectManuallyAsync(endpoint);
            DialogResult = true;
        }
        catch (ProtocolException ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
            Spinner.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBox.Visibility = Visibility.Visible;
    }
}
