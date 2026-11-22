using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SecureSync.Core.Sync;

namespace SecureSync.App.Views;

/// <summary>Live view of syncing: overall progress, connected computers, and files that need attention.</summary>
public partial class SyncActivityWindow : Window
{
    private readonly SyncEngine _engine;
    private readonly DispatcherTimer _timer;

    private sealed record PeerRow(string Name, string Address, string State, Brush StateBrush);

    public SyncActivityWindow(SyncEngine engine)
    {
        InitializeComponent();
        _engine = engine;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        Loaded += (_, _) => Refresh();
        Closed += (_, _) => _timer.Stop();
    }

    private void Refresh()
    {
        var status = _engine.GetStatus();
        var progress = status.Progress;
        var success = (Brush)FindResource("SuccessBrush");
        var accent = (Brush)FindResource("AccentBrush");
        var muted = (Brush)FindResource("MutedBrush");

        StateDot.Visibility = status.State is SyncState.Off or SyncState.Searching ? Visibility.Visible : Visibility.Collapsed;
        StateDot.Fill = status.State == SyncState.Searching ? new SolidColorBrush(Color.FromRgb(0xCA, 0x8A, 0x04)) : (Brush)FindResource("FaintBrush");
        StateGlyph.Text = status.State switch
        {
            SyncState.Synced => status.Problems.Count > 0 ? "" : "",
            SyncState.Syncing => "",
            _ => "",
        };
        StateGlyph.Foreground = status.State == SyncState.Synced ? (status.Problems.Count > 0 ? (Brush)FindResource("WarningBrush") : success) : accent;
        StateText.Text = status.State switch
        {
            SyncState.Synced => "Everything is synced",
            SyncState.Syncing => "Syncing…",
            SyncState.Searching => "Looking for your other computer…",
            _ => status.Mode == SyncMode.Paused ? "Sync is stopped" : "Not syncing — click Sync to start",
        };

        ProgressSection.Visibility = progress.IsActive || status.State == SyncState.Synced ? Visibility.Visible : Visibility.Collapsed;
        Progress.Value = progress.Fraction;
        PercentText.Text = $"{progress.Fraction:P0}";
        ProgressText.Text = progress.IsActive
            ? $"{progress.FilesRemaining:N0} {(progress.FilesRemaining == 1 ? "file" : "files")} left · {FileRow.FormatBytes(progress.BytesDone)} of {FileRow.FormatBytes(progress.BytesDone + progress.BytesRemaining)} transferred"
            : "Nothing left to transfer.";

        PeerList.ItemsSource = status.Peers
            .Select(p => new PeerRow(p.DeviceName, p.Address, p.IsSynced ? "✓ Up to date" : "Syncing…", p.IsSynced ? success : accent))
            .ToList();
        NoPeersText.Visibility = status.Peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoPeersText.Text = status.Mode switch
        {
            SyncMode.Active => "None yet. Unlock SecureSync on your other computer (same Wi-Fi). If they still don't find each other, use Connect by IP address.",
            SyncMode.Paused => "None — sync is stopped.",
            _ => "None. Click Sync in the main window to look for your other computer.",
        };

        ProblemList.ItemsSource = status.Problems;
        NoProblemsText.Visibility = status.Problems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ThisComputerText.Text = $"{string.Join(", ", ExplorerView.LocalAddresses())} · port {_engine.ListenPort}" +
                                (_engine.DiscoveryAvailable ? "" : " · automatic discovery unavailable on this network");
        _ = muted;
    }

    private void OnRetry(object sender, RoutedEventArgs e)
    {
        _engine.RetryNow();
        Refresh();
    }

    private void OnConnect(object sender, RoutedEventArgs e) => new ConnectDialog(_engine) { Owner = this }.ShowDialog();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
