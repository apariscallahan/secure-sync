using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SecureSync.Core.Crypto;
using SecureSync.Core.Security;
using SecureSync.Core.Storage;

namespace SecureSync.App.Views;

/// <summary>Locked screen: unlock with Windows Hello, or first-time setup (create or join a chain).</summary>
public partial class LockView : UserControl
{
    private enum Page { Unlock, Welcome, Create, Join, Key, NoHello }

    private readonly MainWindow _host;
    private LocalProfile? _profile;
    private Page _page;
    private ChainKey? _newKey;
    private Vault? _newVault;
    private bool _offerKeyEntry;

    public LockView(MainWindow host, string? message = null)
    {
        InitializeComponent();
        _host = host;
        _profile = LocalProfile.Load(host.ProfilePath);
        FolderBox.Text = DefaultFolder();
        DeviceBox.Text = Environment.MachineName;
        SetPage(_profile is null ? Page.Welcome : Page.Unlock);
        if (message is not null) ShowError(message);
        Loaded += async (_, _) =>
        {
            if (_page == Page.Unlock) PrimaryButton.Focus();
            if (_profile is null && await _host.Signer.CheckAvailabilityAsync() != SignerAvailability.Available) SetPage(Page.NoHello);
        };
    }

    internal static string DefaultFolder()
    {
        // Not Documents: OneDrive often syncs Documents to the cloud.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var name = PathSafety.MakeUnique("SecureSync", n =>
        {
            var path = Path.Combine(home, n);
            return Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
        }, isFolder: true);
        return Path.Combine(home, name);
    }

    private void SetPage(Page page)
    {
        _page = page;
        ErrorBox.Visibility = Visibility.Collapsed;
        UnlockPage.Visibility = Show(page == Page.Unlock);
        WelcomePage.Visibility = Show(page == Page.Welcome);
        SetupPage.Visibility = Show(page is Page.Create or Page.Join);
        JoinKeyFields.Visibility = Show(page == Page.Join);
        KeyPage.Visibility = Show(page == Page.Key);
        NoHelloPage.Visibility = Show(page == Page.NoHello);
        FooterLink.Visibility = Visibility.Collapsed;
        FooterBar.Visibility = Visibility.Visible;
        SecondaryButton.Visibility = Visibility.Visible;
        PrimaryButton.Visibility = Visibility.Visible;
        PrimaryButton.IsEnabled = true;

        switch (page)
        {
            case Page.Unlock:
                TitleText.Text = "SecureSync is locked";
                SubtitleText.Text = "Unlock with Windows Hello to open your files and sync.";
                UnlockChainText.Text = _profile!.Fingerprint;
                UnlockDeviceText.Text = _profile.DeviceName;
                UnlockFolderText.Text = _profile.Folder;
                UnlockFolderText.ToolTip = _profile.Folder;
                Primary("", "Unlock with Windows Hello");
                SecondaryButton.Visibility = Visibility.Collapsed;
                Footer(_offerKeyEntry ? "Enter the sync chain key instead…" : "Use a different sync chain…");
                break;
            case Page.Welcome:
                TitleText.Text = "Set up SecureSync";
                SubtitleText.Text = "SecureSync keeps an encrypted folder in sync between your computers.";
                PrimaryButton.Visibility = Visibility.Collapsed;
                SecondaryButton.Visibility = Visibility.Collapsed;
                if (_profile is not null) Footer("Back to unlock");
                FooterBar.Visibility = Show(_profile is not null);
                return;
            case Page.Create:
                TitleText.Text = "Create a new sync chain";
                SubtitleText.Text = "Choose where this computer keeps its encrypted files.";
                Primary("", "Create");
                SecondaryButton.Content = "Back";
                break;
            case Page.Join:
                TitleText.Text = "Join a sync chain";
                SubtitleText.Text = "Enter the sync chain key from your first computer.";
                Primary("", "Join");
                SecondaryButton.Content = "Back";
                break;
            case Page.Key:
                TitleText.Text = "Save your sync chain key";
                SubtitleText.Text = "Enter this key on your other computers to join this sync chain.";
                Primary("", "Continue");
                PrimaryButton.IsEnabled = SavedKeyCheck.IsChecked == true;
                SecondaryButton.Visibility = Visibility.Collapsed;
                break;
            case Page.NoHello:
                TitleText.Text = "Windows Hello isn't set up";
                SubtitleText.Text = "SecureSync needs your Windows Hello PIN, fingerprint or face.";
                Primary("", "Try again");
                SecondaryButton.Content = "Open sign-in settings";
                break;
        }
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void Primary(string glyph, string text)
    {
        PrimaryIcon.Text = glyph;
        PrimaryText.Text = text;
    }

    private void Footer(string text)
    {
        FooterLink.Content = text;
        FooterLink.Visibility = Visibility.Visible;
    }

    // ---- Buttons ---------------------------------------------------------------------------------

    private async void OnPrimary(object sender, RoutedEventArgs e)
    {
        switch (_page)
        {
            case Page.Unlock: await UnlockAsync(); break;
            case Page.Create: await CreateAsync(); break;
            case Page.Join: await JoinAsync(); break;
            case Page.Key: FinishCreate(); break;
            case Page.NoHello:
                if (await _host.Signer.CheckAvailabilityAsync() == SignerAvailability.Available) SetPage(_profile is null ? Page.Welcome : Page.Unlock);
                else ShowError("Windows Hello still isn't available. Set up a PIN in Settings › Accounts › Sign-in options.");
                break;
        }
    }

    private void OnSecondary(object sender, RoutedEventArgs e)
    {
        if (_page == Page.NoHello)
            Process.Start(new ProcessStartInfo("ms-settings:signinoptions") { UseShellExecute = true });
        else
            SetPage(Page.Welcome);
    }

    private void OnFooterLink(object sender, RoutedEventArgs e)
    {
        if (_page == Page.Welcome)
        {
            SetPage(Page.Unlock);
            return;
        }
        if (_offerKeyEntry)
        {
            // Re-protect the key after Windows Hello was reset: same folder and name, typed-in key.
            FolderBox.Text = _profile!.Folder;
            DeviceBox.Text = _profile.DeviceName;
            SetPage(Page.Join);
            return;
        }
        var answer = MessageBox.Show(Window.GetWindow(this),
            "This computer will forget its sync chain key. Your encrypted folder stays where it is.\n\n" +
            "Only do this if you have the sync chain key saved somewhere — you'll need it to get back into this chain.\n\nContinue?",
            "Use a different sync chain", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        try { File.Delete(_host.ProfilePath); } catch (IOException) { }
        _profile = null;
        SetPage(Page.Welcome);
    }

    private void OnChooseCreate(object sender, RoutedEventArgs e)
    {
        SetPage(Page.Create);
        FolderBox.Focus();
    }

    private void OnChooseJoin(object sender, RoutedEventArgs e)
    {
        SetPage(Page.Join);
        JoinKeyBox.Focus();
    }

    // ---- Unlock ----------------------------------------------------------------------------------

    private async Task UnlockAsync()
    {
        var profile = _profile!;
        SetBusy("Waiting for Windows Hello…");
        try
        {
            using var key = await KeyProtection.UnsealAsync(profile, _host.Signer, _host.Protector);
            if (!Directory.Exists(profile.Folder) && !ResolveMissingFolder(ref profile)) return;
            SetBusy("Opening…");
            var vault = await Task.Run(() => Vault.Open(profile.Folder, key, profile.DeviceName));
            _host.OnUnlocked(vault);
        }
        catch (KeyStoreCanceledException)
        {
            ShowError("Windows Hello verification was canceled.");
        }
        catch (KeyStoreException ex)
        {
            ShowError(ex.Message);
            _offerKeyEntry = true;
            Footer("Enter the sync chain key instead…");
        }
        catch (Exception ex) when (ex is VaultException or IOException or UnauthorizedAccessException)
        {
            ShowError(ex.Message);
        }
        finally
        {
            ClearBusy();
        }
    }

    /// <summary>The vault folder was moved or deleted: recreate it (contents re-download) or point to its new place.</summary>
    private bool ResolveMissingFolder(ref LocalProfile profile)
    {
        var answer = MessageBox.Show(Window.GetWindow(this),
            $"The folder with your encrypted files wasn't found:\n{profile.Folder}\n\n" +
            "Yes — create it again (your files download again from your other computer)\nNo — choose where the folder is now",
            "Folder not found", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.No)
        {
            var dialog = new OpenFolderDialog { Title = "Where is the folder with your encrypted files?" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return false;
            profile = profile with { Folder = dialog.FolderName };
            profile.Save(_host.ProfilePath);
            _profile = profile;
        }
        return true;
    }

    // ---- Create ----------------------------------------------------------------------------------

    private async Task CreateAsync()
    {
        if (!TryGetFolder(out var folder)) return;
        if (Vault.ReadFingerprint(folder) is { } existing)
        {
            ShowError($"That folder already belongs to sync chain {existing}. Choose an empty folder, or join that chain instead.");
            return;
        }
        if (!ConfirmNonEmptyFolder(folder)) return;
        var deviceName = DeviceName();
        var key = ChainKey.Generate();
        try
        {
            SetBusy("Waiting for Windows Hello…");
            var profile = await KeyProtection.SealAsync(key, folder, deviceName, _host.Signer, _host.Protector);
            SetBusy("Creating…");
            var vault = await Task.Run(() => Vault.Open(folder, key, deviceName));
            profile.Save(_host.ProfilePath);
            _profile = profile;
            _newKey = key;
            _newVault = vault;
            KeyText.Text = key.Reveal();
            SetPage(Page.Key);
        }
        catch (KeyStoreCanceledException)
        {
            key.Dispose();
            ShowError("Windows Hello verification was canceled. SecureSync needs it to protect the key on this computer.");
        }
        catch (Exception ex) when (ex is KeyStoreException or VaultException or IOException or UnauthorizedAccessException)
        {
            key.Dispose();
            ShowError(ex.Message);
        }
        finally
        {
            ClearBusy();
        }
    }

    private void OnCopyKey(object sender, RoutedEventArgs e) => Clipboard.SetText(KeyText.Text);

    private void OnSaveKeyFile(object sender, RoutedEventArgs e)
    {
        if (_newKey is null) return;
        SaveKeyFile(Window.GetWindow(this), _newKey);
    }

    internal static void SaveKeyFile(Window? owner, ChainKey key)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save sync chain key",
            FileName = $"SecureSync key {key.Fingerprint}.sskey",
            Filter = "SecureSync key file (*.sskey)|*.sskey|Text file (*.txt)|*.txt",
        };
        if (dialog.ShowDialog(owner) != true) return;
        File.WriteAllText(dialog.FileName,
            $"SecureSync sync chain key\r\n\r\n{key.Reveal()}\r\n\r\nChain: {key.Fingerprint}\r\n" +
            "Keep this file private: anyone who has it can read the files in this sync chain.\r\n");
    }

    private void OnSavedKeyChanged(object sender, RoutedEventArgs e)
    {
        if (_page == Page.Key) PrimaryButton.IsEnabled = SavedKeyCheck.IsChecked == true;
    }

    private void FinishCreate()
    {
        var vault = _newVault!;
        _newKey?.Dispose();
        _newKey = null;
        _newVault = null;
        KeyText.Clear();
        _host.OnUnlocked(vault);
    }

    // ---- Join ------------------------------------------------------------------------------------

    private void OnJoinKeyChanged(object sender, TextChangedEventArgs e)
    {
        var text = new string(JoinKeyBox.Text.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        if (text.Length == 0)
        {
            JoinKeyHint.Text = " ";
            return;
        }
        if (ChainKey.TryParse(text, out var key))
        {
            JoinKeyHint.Text = $"✓ Valid key for sync chain {key!.Fingerprint}";
            JoinKeyHint.Foreground = (Brush)FindResource("SuccessBrush");
            key.Dispose();
        }
        else
        {
            bool tooShort = text.Length < ChainKey.TextLength;
            JoinKeyHint.Text = tooShort ? $"{text.Length} of {ChainKey.TextLength} characters" : "This key has a typo — check it against your first computer.";
            JoinKeyHint.Foreground = (Brush)FindResource(tooShort ? "MutedBrush" : "DangerBrush");
        }
    }

    private void OnOpenKeyFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Open sync chain key file", Filter = "SecureSync key file (*.sskey;*.txt)|*.sskey;*.txt|All files|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var found = File.ReadLines(dialog.FileName).Take(200).FirstOrDefault(line => ChainKey.TryParse(line, out var k) && Dispose(k));
            if (found is null) ShowError("That file doesn't contain a SecureSync sync chain key.");
            else JoinKeyBox.Text = found.Trim();
        }
        catch (IOException ex)
        {
            ShowError(ex.Message);
        }

        static bool Dispose(ChainKey? key)
        {
            key?.Dispose();
            return true;
        }
    }

    private async Task JoinAsync()
    {
        if (!ChainKey.TryParse(JoinKeyBox.Text, out var key))
        {
            ShowError("Enter the full sync chain key (52 characters starting with \"SS\") from your first computer.");
            JoinKeyBox.Focus();
            return;
        }
        using (key)
        {
            if (!TryGetFolder(out var folder)) return;
            var existing = Vault.ReadFingerprint(folder);
            if (existing is not null && existing != key!.Fingerprint)
            {
                ShowError($"That folder belongs to a different sync chain ({existing}). Choose an empty folder.");
                return;
            }
            if (existing is null && !ConfirmNonEmptyFolder(folder)) return;
            var deviceName = DeviceName();
            try
            {
                SetBusy("Waiting for Windows Hello…");
                var profile = await KeyProtection.SealAsync(key!, folder, deviceName, _host.Signer, _host.Protector);
                SetBusy("Opening…");
                var vault = await Task.Run(() => Vault.Open(folder, key!, deviceName));
                profile.Save(_host.ProfilePath);
                _profile = profile;
                JoinKeyBox.Clear();
                _host.OnUnlocked(vault);
            }
            catch (KeyStoreCanceledException)
            {
                ShowError("Windows Hello verification was canceled. SecureSync needs it to protect the key on this computer.");
            }
            catch (Exception ex) when (ex is KeyStoreException or VaultException or IOException or UnauthorizedAccessException)
            {
                ShowError(ex.Message);
            }
            finally
            {
                ClearBusy();
            }
        }
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder for your encrypted files" };
        var current = FolderBox.Text.Trim();
        if (Directory.Exists(current)) dialog.InitialDirectory = current;
        else if (Path.GetDirectoryName(current) is { } parent && Directory.Exists(parent)) dialog.InitialDirectory = parent;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) FolderBox.Text = dialog.FolderName;
    }

    private bool TryGetFolder(out string folder)
    {
        folder = FolderBox.Text.Trim().Trim('"');
        try
        {
            if (folder.Length == 0) throw new ArgumentException();
            folder = Path.GetFullPath(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ShowError("Choose a folder for the encrypted files.");
            FolderBox.Focus();
            return false;
        }
        if (Path.GetPathRoot(folder) == folder)
        {
            ShowError("Choose a folder, not the root of a drive.");
            return false;
        }
        return true;
    }

    private bool ConfirmNonEmptyFolder(string folder)
    {
        if (!Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any()) return true;
        return MessageBox.Show(Window.GetWindow(this),
            $"\"{folder}\" already contains other files.\n\nSecureSync will keep its encrypted files alongside them and won't touch the existing ones, but an empty folder is tidier.\n\nUse this folder anyway?",
            "Folder isn't empty", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private string DeviceName() => string.IsNullOrWhiteSpace(DeviceBox.Text) ? Environment.MachineName : DeviceBox.Text.Trim();

    private void SetBusy(string text)
    {
        BusyText.Text = text;
        BusyPanel.Visibility = Visibility.Visible;
        PrimaryButton.IsEnabled = false;
        SecondaryButton.IsEnabled = false;
        ErrorBox.Visibility = Visibility.Collapsed;
    }

    private void ClearBusy()
    {
        BusyPanel.Visibility = Visibility.Collapsed;
        PrimaryButton.IsEnabled = _page != Page.Key || SavedKeyCheck.IsChecked == true;
        SecondaryButton.IsEnabled = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBox.Visibility = Visibility.Visible;
    }
}
