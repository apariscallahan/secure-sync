using SecureSync.Core.Security;
using SecureSync.Core.Storage;
using SecureSync.Core.Sync;

namespace SecureSync.Mobile;

/// <summary>
/// The unlocked session on the phone, and its lifecycle. To keep battery use minimal:
///   • syncing runs only while the app is on screen — it stops the moment the app goes to the background
///     (no background service, no scheduled jobs, no polling) and resumes when you come back;
///   • after more than <see cref="LockAfter"/> in the background, the key is forgotten and you unlock again.
/// Anything not finished when the app was closed simply continues the next time devices meet.
/// </summary>
public sealed class AppState
{
    public static AppState Current { get; } = new();
    public static readonly TimeSpan LockAfter = TimeSpan.FromMinutes(5);

    private DateTime? _backgroundedAt;

    public string ProfilePath => Path.Combine(FileSystem.Current.AppDataDirectory, "profile.json");
    public string VaultFolder => Path.Combine(FileSystem.Current.AppDataDirectory, "vault");
    public IKeySigner Signer { get; } = new BiometricSigner();

    public Vault? Vault { get; private set; }
    public SyncEngine? Engine { get; private set; }

    /// <summary>Raised on the UI thread when the sync engine starts or stops (pages re-subscribe to it).</summary>
    public event EventHandler? EngineChanged;

    public LocalProfile? LoadProfile() => LocalProfile.Load(ProfilePath);

    public void OnUnlocked(Vault vault)
    {
        Vault = vault;
        StartSync();
        Application.Current!.Windows[0].Page = new NavigationPage(new FilesPage(null));
    }

    public void StartSync()
    {
        if (Vault is null || Engine is not null) return;
        var profile = LoadProfile();
        var engine = new SyncEngine(Vault, new SyncOptions { RememberedPeers = profile?.RememberedEndPoints() ?? [] });
        engine.ManualPeerConnected += (_, endpoint) => LocalProfile.Remember(ProfilePath, endpoint);
        try
        {
            engine.Start();
        }
        catch (System.Net.Sockets.SocketException)
        {
            return; // no network right now; tried again when the app comes back to the foreground
        }
        NetworkSupport.BeginDiscovery();
        Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;
        engine.SetMode(profile?.SyncEnabled == false ? SyncMode.Paused : SyncMode.Active);
        Engine = engine;
        EngineChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task StopSyncAsync()
    {
        if (Engine is not { } engine) return;
        Engine = null;
        Connectivity.Current.ConnectivityChanged -= OnConnectivityChanged;
        await engine.DisposeAsync();
        NetworkSupport.EndDiscovery();
        EngineChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetSyncEnabled(bool enabled)
    {
        Engine?.SetMode(enabled ? SyncMode.Active : SyncMode.Paused);
        if (LoadProfile() is { } profile && profile.SyncEnabled != enabled)
            (profile with { SyncEnabled = enabled }).Save(ProfilePath);
    }

    public async Task LockAsync()
    {
        await StopSyncAsync();
        Vault?.Dispose();
        Vault = null;
        Application.Current!.Windows[0].Page = new NavigationPage(new LockPage());
    }

    public void OnBackground()
    {
        _backgroundedAt = DateTime.UtcNow;
        _ = StopSyncAsync();
    }

    public async Task OnForegroundAsync()
    {
        var away = _backgroundedAt is { } since ? DateTime.UtcNow - since : TimeSpan.Zero;
        _backgroundedAt = null;
        if (Vault is null) return;
        if (away > LockAfter) await LockAsync();
        else StartSync();
    }

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e) => Engine?.NudgeDiscovery();
}
