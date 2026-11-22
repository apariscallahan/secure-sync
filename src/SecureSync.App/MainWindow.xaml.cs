using System.ComponentModel;
using System.Net.Sockets;
using System.Windows;
using SecureSync.App.Security;
using SecureSync.App.Views;
using SecureSync.Core.Security;
using SecureSync.Core.Storage;
using SecureSync.Core.Sync;

namespace SecureSync.App;

public partial class MainWindow : Window
{
    private Vault? _vault;
    private SyncEngine? _engine;

    public MainWindow() : this(new WindowsHelloSigner(), LocalProfile.DefaultPath) { }

    internal MainWindow(IKeySigner signer, string profilePath)
    {
        InitializeComponent();
        Signer = signer;
        ProfilePath = profilePath;
        ShowLock();
    }

    internal IKeySigner Signer { get; }
    internal ILocalProtector Protector { get; } = new DpapiProtector();
    internal string ProfilePath { get; }
    internal SyncEngine? Engine => _engine;
    internal Vault? CurrentVault => _vault;

    public void ShowLock(string? message = null)
    {
        Title = "SecureSync";
        Host.Content = new LockView(this, message);
    }

    /// <summary>Called by the lock screen once the chain key is unsealed and the vault folder is open.</summary>
    public void OnUnlocked(Vault vault)
    {
        var profile = LocalProfile.Load(ProfilePath);
        var engine = new SyncEngine(vault, new SyncOptions { RememberedPeers = profile?.RememberedEndPoints() ?? [] });
        engine.ManualPeerConnected += (_, endpoint) => LocalProfile.Remember(ProfilePath, endpoint);
        try
        {
            engine.Start();
        }
        catch (SocketException e)
        {
            vault.Dispose();
            ShowLock($"Couldn't start networking: {e.Message}");
            return;
        }
        _vault = vault;
        _engine = engine;
        // Sync starts by itself on unlock (so devices find each other without anyone pressing Sync), unless the
        // user turned it off last time.
        engine.SetMode(profile?.SyncEnabled == false ? SyncMode.Paused : SyncMode.Active);
        Title = $"SecureSync — {vault.DeviceName}";
        Host.Content = new ExplorerView(this, vault, engine);
    }

    /// <summary>Remembers whether this computer should sync automatically after unlocking.</summary>
    internal void SetSyncEnabled(bool enabled)
    {
        if (LocalProfile.Load(ProfilePath) is { } profile && profile.SyncEnabled != enabled)
            (profile with { SyncEnabled = enabled }).Save(ProfilePath);
    }

    /// <summary>Stops syncing, wipes keys from memory and deletes decrypted temp copies.</summary>
    public async Task LockAsync()
    {
        (Host.Content as ExplorerView)?.Detach();
        if (_engine is not null) await _engine.DisposeAsync();
        _vault?.Dispose();
        _engine = null;
        _vault = null;
        ShowLock();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (Host.Content is ExplorerView { IsBusy: true })
        {
            var answer = MessageBox.Show(this, "Files are still being encrypted or saved. Quit anyway?", "SecureSync",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        (Host.Content as ExplorerView)?.Detach();
        _engine?.DisposeAsync().AsTask().Wait(2000);
        _vault?.Dispose();
        base.OnClosing(e);
    }
}
