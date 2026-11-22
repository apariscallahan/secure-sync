using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SecureSync.Core.Sync;

namespace SecureSync.Mobile;

/// <summary>Sync activity: progress, connected devices, files that need attention, and the on/off switch.</summary>
public sealed class SyncPage : ContentPage
{
    private readonly AppState _app = AppState.Current;
    private readonly Label _state = new() { Style = Res<Style>("Title"), FontSize = 20 };
    private readonly ProgressBar _progress = new();
    private readonly Label _progressText = new() { Style = Res<Style>("Muted") };
    private readonly VerticalStackLayout _devices = new() { Spacing = 10 };
    private readonly VerticalStackLayout _problems = new() { Spacing = 10 };
    private readonly Switch _syncSwitch = new();
    private readonly Label _thisPhone = new() { Style = Res<Style>("Muted"), FontSize = 13 };
    private SyncEngine? _engine;
    private int _updateQueued;
    private bool _settingSwitch;

    public SyncPage()
    {
        Title = "Sync activity";
        var retry = new Button { Text = "Retry now" };
        retry.Clicked += (_, _) =>
        {
            _app.Engine?.RetryNow();
            Update();
        };
        var connect = new Button { Text = "Connect by IP address", Style = Res<Style>("Outlined") };
        connect.Clicked += OnConnectByIp;
        _syncSwitch.Toggled += (_, e) =>
        {
            if (_settingSwitch) return;
            _app.SetSyncEnabled(e.Value);
            Update();
        };

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(24, 16, 24, 32),
                Spacing = 8,
                Children =
                {
                    _state,
                    _progress,
                    _progressText,
                    new Grid
                    {
                        Margin = new Thickness(0, 16, 0, 0),
                        ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) },
                        Children =
                        {
                            new VerticalStackLayout
                            {
                                Children =
                                {
                                    new Label { Text = "Sync on this phone", FontAttributes = FontAttributes.Bold },
                                    new Label { Text = "Only while SecureSync is open — never in the background.", Style = Res<Style>("Muted"), FontSize = 13 },
                                },
                            },
                            _syncSwitch,
                        },
                    },
                    new Label { Text = "CONNECTED DEVICES", Style = Res<Style>("Section") },
                    _devices,
                    new Label { Text = "NEEDS ATTENTION", Style = Res<Style>("Section") },
                    _problems,
                    new Label { Text = "THIS PHONE", Style = Res<Style>("Section") },
                    _thisPhone,
                    new VerticalStackLayout { Spacing = 12, Margin = new Thickness(0, 20, 0, 0), Children = { retry, connect } },
                },
            },
        };
        Grid.SetColumn(_syncSwitch, 1);
    }

    private static T Res<T>(string key) => (T)Application.Current!.Resources[key];

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _app.EngineChanged += OnEngineChanged;
        Attach();
        Update();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _app.EngineChanged -= OnEngineChanged;
        if (_engine is not null) _engine.StateChanged -= OnStateChanged;
        _engine = null;
    }

    private void OnEngineChanged(object? sender, EventArgs e)
    {
        Attach();
        Update();
    }

    private void Attach()
    {
        if (_engine is not null) _engine.StateChanged -= OnStateChanged;
        _engine = _app.Engine;
        if (_engine is not null) _engine.StateChanged += OnStateChanged;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _updateQueued, 1) == 1) return;
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(300), () =>
        {
            Interlocked.Exchange(ref _updateQueued, 0);
            Update();
        });
    }

    private void Update()
    {
        var status = _engine?.GetStatus();
        _settingSwitch = true;
        _syncSwitch.IsToggled = status?.Mode != SyncMode.Paused && _app.LoadProfile()?.SyncEnabled != false;
        _settingSwitch = false;

        _state.Text = status?.State switch
        {
            SyncState.Synced when status.Problems.Count > 0 => "Synced, with problems",
            SyncState.Synced => "Everything is synced",
            SyncState.Syncing => "Syncing…",
            SyncState.Searching => "Looking for your other devices…",
            _ => status?.Mode == SyncMode.Paused ? "Sync is off on this phone" : "Not syncing",
        };
        var progress = status?.Progress;
        _progress.IsVisible = progress?.IsActive == true;
        _progress.Progress = progress?.Fraction ?? 0;
        _progressText.Text = progress?.IsActive == true
            ? $"{progress.FilesRemaining} files left · {ItemRow.FormatSize(progress.BytesDone)} of {ItemRow.FormatSize(progress.BytesDone + progress.BytesRemaining)} · {progress.Fraction:P0}"
            : "Nothing left to transfer.";

        _devices.Children.Clear();
        foreach (var peer in status?.Peers ?? [])
        {
            _devices.Children.Add(new HorizontalStackLayout
            {
                Spacing = 12,
                Children =
                {
                    new Image { Source = "ic_devices.png", WidthRequest = 24, HeightRequest = 24 },
                    new VerticalStackLayout
                    {
                        Children =
                        {
                            new Label { Text = peer.DeviceName },
                            new Label { Text = $"{peer.Address} · {(peer.IsSynced ? "up to date" : "syncing…")}", Style = Res<Style>("Muted"), FontSize = 13 },
                        },
                    },
                },
            });
        }
        if (_devices.Children.Count == 0)
            _devices.Children.Add(new Label
            {
                Style = Res<Style>("Muted"),
                Text = "None right now. Open SecureSync on a computer on the same Wi-Fi. If they don't find each other, use Connect by IP address.",
            });

        _problems.Children.Clear();
        foreach (var problem in status?.Problems ?? [])
        {
            _problems.Children.Add(new VerticalStackLayout
            {
                Children =
                {
                    new Label { Text = problem.FileName, FontAttributes = FontAttributes.Bold },
                    new Label { Text = problem.Message, Style = Res<Style>("Muted"), FontSize = 13 },
                },
            });
        }
        if (_problems.Children.Count == 0)
            _problems.Children.Add(new Label
            {
                Style = Res<Style>("Muted"),
                Text = "Nothing. Failed transfers are retried automatically — from another device if one is connected.",
            });

        _thisPhone.Text = $"{_app.Vault?.DeviceName} · {string.Join(", ", LocalAddresses())}" + (_engine is null ? "" : $" · port {_engine.ListenPort}");
    }

    private static IEnumerable<string> LocalAddresses()
    {
        try
        {
            var addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .ToList();
            return addresses.Count > 0 ? addresses : ["no Wi-Fi"];
        }
        catch (Exception)
        {
            return ["address unavailable"];
        }
    }

    private async void OnConnectByIp(object? sender, EventArgs e)
    {
        if (_engine is null)
        {
            await DisplayAlertAsync("Sync is off", "Turn on Sync on this phone first.", "OK");
            return;
        }
        var text = await DisplayPromptAsync("Connect by IP address",
            "Enter your computer's IP address. In SecureSync on the computer, hover over the sync status to see it.",
            "Connect", "Cancel", placeholder: "192.168.1.20", keyboard: Keyboard.Url);
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!IPEndPoint.TryParse(text.Trim(), out var endpoint) || endpoint.AddressFamily != AddressFamily.InterNetwork)
        {
            await DisplayAlertAsync("Connect by IP address", "That isn't an IPv4 address like 192.168.1.20.", "OK");
            return;
        }
        if (endpoint.Port == 0) endpoint.Port = 47821;
        try
        {
            await _engine.ConnectManuallyAsync(endpoint);
        }
        catch (ProtocolException ex)
        {
            await DisplayAlertAsync("Couldn't connect", ex.Message, "OK");
        }
    }
}
