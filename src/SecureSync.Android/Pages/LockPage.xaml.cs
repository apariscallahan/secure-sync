using SecureSync.Core.Crypto;
using SecureSync.Core.Security;
using SecureSync.Core.Storage;

namespace SecureSync.Mobile;

/// <summary>Locked screen: unlock with fingerprint/face/screen lock, or first-time setup (create or join a chain).</summary>
public partial class LockPage : ContentPage
{
    private enum Mode { Unlock, Welcome, Create, Join, Key, NoLock }

    private readonly AppState _app = AppState.Current;
    private LocalProfile? _profile;
    private Mode _mode;
    private ChainKey? _newKey;
    private Vault? _newVault;
    private bool _offerKeyEntry;
    private bool _autoPrompted;

    public LockPage()
    {
        InitializeComponent();
        _profile = _app.LoadProfile();
        DeviceEntry.Text = DeviceInfo.Current.Name;
        SetMode(_profile is null ? Mode.Welcome : Mode.Unlock);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_profile is null && await _app.Signer.CheckAvailabilityAsync() != SignerAvailability.Available)
        {
            SetMode(Mode.NoLock);
            return;
        }
        // Like other apps with fingerprint unlock: ask straight away.
        if (_mode == Mode.Unlock && !_autoPrompted)
        {
            _autoPrompted = true;
            await UnlockAsync();
        }
    }

    private void SetMode(Mode mode)
    {
        _mode = mode;
        ErrorBox.IsVisible = false;
        UnlockPanel.IsVisible = mode == Mode.Unlock;
        WelcomePanel.IsVisible = mode == Mode.Welcome;
        SetupPanel.IsVisible = mode is Mode.Create or Mode.Join;
        JoinFields.IsVisible = mode == Mode.Join;
        KeyPanel.IsVisible = mode == Mode.Key;
        NoLockPanel.IsVisible = mode == Mode.NoLock;
        FooterButton.IsVisible = false;

        switch (mode)
        {
            case Mode.Unlock:
                TitleLabel.Text = "SecureSync is locked";
                SubtitleLabel.Text = "Unlock with your fingerprint, face or screen lock.";
                ChainLabel.Text = _profile!.Fingerprint;
                DeviceLabel.Text = _profile.DeviceName;
                Footer(_offerKeyEntry ? "Enter the sync chain key instead" : "Use a different sync chain");
                break;
            case Mode.Welcome:
                TitleLabel.Text = "Set up SecureSync";
                SubtitleLabel.Text = "An encrypted folder that stays in sync between your phone and computers.";
                if (_profile is not null) Footer("Back to unlock");
                break;
            case Mode.Create:
                TitleLabel.Text = "Create a sync chain";
                SubtitleLabel.Text = "Your files will be encrypted on this phone and synced with your other devices.";
                SetupButton.Text = "Create";
                break;
            case Mode.Join:
                TitleLabel.Text = "Join a sync chain";
                SubtitleLabel.Text = "Enter the sync chain key from your computer. (In SecureSync on the computer: Help › Show sync chain key.)";
                SetupButton.Text = "Join";
                break;
            case Mode.Key:
                TitleLabel.Text = "Save your sync chain key";
                SubtitleLabel.Text = "Enter this key on your other devices to join this sync chain.";
                break;
            case Mode.NoLock:
                TitleLabel.Text = "Set up a screen lock";
                SubtitleLabel.Text = "SecureSync needs your fingerprint, face or screen lock.";
                break;
        }
    }

    private void Footer(string text)
    {
        FooterButton.Text = text;
        FooterButton.IsVisible = true;
    }

    // ---- Unlock ----------------------------------------------------------------------------------

    private async void OnUnlock(object? sender, EventArgs e) => await UnlockAsync();

    private async Task UnlockAsync()
    {
        var profile = _profile!;
        SetBusy("Waiting for verification…");
        try
        {
            using var key = await KeyProtection.UnsealAsync(profile, _app.Signer);
            SetBusy("Opening…");
            var vault = await Task.Run(() => Vault.Open(profile.Folder, key, profile.DeviceName));
            _app.OnUnlocked(vault);
        }
        catch (KeyStoreCanceledException)
        {
            // stay on the lock screen; the Unlock button tries again
        }
        catch (KeyStoreException ex)
        {
            ShowError(ex.Message);
            _offerKeyEntry = true;
            Footer("Enter the sync chain key instead");
        }
        catch (Exception ex) when (ex is VaultException or IOException)
        {
            ShowError(ex.Message);
        }
        finally
        {
            ClearBusy();
        }
    }

    // ---- Setup -----------------------------------------------------------------------------------

    private void OnChooseCreate(object? sender, EventArgs e) => SetMode(Mode.Create);

    private void OnChooseJoin(object? sender, EventArgs e) => SetMode(Mode.Join);

    private void OnBack(object? sender, EventArgs e) => SetMode(Mode.Welcome);

    private async void OnPasteKey(object? sender, EventArgs e)
    {
        if (await Clipboard.Default.GetTextAsync() is { } text) KeyEditor.Text = text.Trim();
    }

    private void OnKeyChanged(object? sender, TextChangedEventArgs e)
    {
        var text = new string((KeyEditor.Text ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        if (text.Length == 0)
        {
            KeyHint.Text = " ";
            return;
        }
        if (ChainKey.TryParse(text, out var key))
        {
            KeyHint.Text = $"✓ Valid key for sync chain {key!.Fingerprint}";
            KeyHint.TextColor = (Color)Application.Current!.Resources["Success"];
            key.Dispose();
        }
        else
        {
            bool tooShort = text.Length < ChainKey.TextLength;
            KeyHint.Text = tooShort ? $"{text.Length} of {ChainKey.TextLength} characters" : "This key has a typo — check it against your computer.";
            KeyHint.TextColor = (Color)Application.Current!.Resources[tooShort ? "MutedColor" : "Danger"];
        }
    }

    private async void OnSetup(object? sender, EventArgs e)
    {
        var deviceName = string.IsNullOrWhiteSpace(DeviceEntry.Text) ? DeviceInfo.Current.Name : DeviceEntry.Text.Trim();
        ChainKey? key;
        if (_mode == Mode.Join)
        {
            if (!ChainKey.TryParse(KeyEditor.Text, out key))
            {
                ShowError("Enter the full sync chain key (52 characters starting with \"SS\").");
                return;
            }
        }
        else
        {
            key = ChainKey.Generate();
        }

        var folder = _app.VaultFolder;
        var existing = Vault.ReadFingerprint(folder);
        if (existing is not null && existing != key!.Fingerprint)
        {
            // This phone was set up for another chain before; its encrypted copy is useless without that key.
            if (!await DisplayAlertAsync("Replace the files on this phone?",
                    $"This phone still has encrypted files from another sync chain ({existing}). They'll be removed. Files on your other devices aren't affected.",
                    "Replace", "Cancel"))
            {
                key!.Dispose();
                return;
            }
            Directory.Delete(folder, recursive: true);
        }

        try
        {
            SetBusy("Waiting for verification…");
            var profile = await KeyProtection.SealAsync(key!, folder, deviceName, _app.Signer);
            SetBusy("Setting up…");
            var vault = await Task.Run(() => Vault.Open(folder, key!, deviceName));
            profile.Save(_app.ProfilePath);
            _profile = profile;
            KeyEditor.Text = "";
            if (_mode == Mode.Create)
            {
                _newKey = key;
                _newVault = vault;
                NewKeyLabel.Text = key!.Reveal();
                SetMode(Mode.Key);
                return;
            }
            key!.Dispose();
            _app.OnUnlocked(vault);
        }
        catch (KeyStoreCanceledException)
        {
            key!.Dispose();
            ShowError("Verification was canceled. SecureSync needs it to protect the key on this phone.");
        }
        catch (Exception ex) when (ex is KeyStoreException or VaultException or IOException)
        {
            key!.Dispose();
            ShowError(ex.Message);
        }
        finally
        {
            ClearBusy();
        }
    }

    private async void OnCopyKey(object? sender, EventArgs e)
    {
        await Clipboard.Default.SetTextAsync(NewKeyLabel.Text);
        await DisplayAlertAsync("Copied", "The key is on the clipboard. Paste it into your password manager, then copy something else so it doesn't stay there.", "OK");
    }

    private async void OnShareKey(object? sender, EventArgs e) =>
        await Share.Default.RequestAsync(new ShareTextRequest { Title = "SecureSync sync chain key", Text = NewKeyLabel.Text });

    private void OnSavedChanged(object? sender, CheckedChangedEventArgs e) => ContinueButton.IsEnabled = e.Value;

    private void OnContinue(object? sender, EventArgs e)
    {
        var vault = _newVault!;
        _newKey?.Dispose();
        _newKey = null;
        _newVault = null;
        NewKeyLabel.Text = "";
        _app.OnUnlocked(vault);
    }

    // ---- Other -----------------------------------------------------------------------------------

    private async void OnFooter(object? sender, EventArgs e)
    {
        if (_mode == Mode.Welcome)
        {
            SetMode(Mode.Unlock);
            return;
        }
        if (_offerKeyEntry)
        {
            DeviceEntry.Text = _profile!.DeviceName;
            SetMode(Mode.Join);
            return;
        }
        if (!await DisplayAlertAsync("Use a different sync chain?",
                "This phone will forget its sync chain key and remove its encrypted copy of the files. Your other devices aren't affected.\n\nYou'll need the other chain's key to continue.",
                "Continue", "Cancel")) return;
        try { File.Delete(_app.ProfilePath); } catch (IOException) { }
        _profile = null;
        SetMode(Mode.Welcome);
    }

    private void OnOpenSecuritySettings(object? sender, EventArgs e)
    {
        var intent = new Android.Content.Intent(Android.Provider.Settings.ActionSecuritySettings);
        intent.AddFlags(Android.Content.ActivityFlags.NewTask);
        Platform.CurrentActivity?.StartActivity(intent);
    }

    private async void OnRetryAvailability(object? sender, EventArgs e)
    {
        if (await _app.Signer.CheckAvailabilityAsync() == SignerAvailability.Available) SetMode(_profile is null ? Mode.Welcome : Mode.Unlock);
        else ShowError("There's still no screen lock on this phone.");
    }

    private void SetBusy(string text)
    {
        BusyLabel.Text = text;
        BusyPanel.IsVisible = true;
        ErrorBox.IsVisible = false;
        UnlockButton.IsEnabled = SetupButton.IsEnabled = false;
    }

    private void ClearBusy()
    {
        BusyPanel.IsVisible = false;
        UnlockButton.IsEnabled = SetupButton.IsEnabled = true;
    }

    private void ShowError(string message)
    {
        ErrorLabel.Text = message;
        ErrorBox.IsVisible = true;
    }
}
