using System.Collections.ObjectModel;
using SecureSync.Core.Security;
using SecureSync.Core.Storage;
using SecureSync.Core.Sync;

namespace SecureSync.Mobile;

/// <summary>One folder of the encrypted drive. Tapping a folder opens another FilesPage on the navigation stack.</summary>
public partial class FilesPage : ContentPage
{
    private readonly AppState _app = AppState.Current;
    private readonly ObjectId? _folder;
    private readonly ObservableCollection<ItemRow> _rows = [];
    private SyncEngine? _engine;
    private int _updateQueued;
    private string? _workText;

    public FilesPage(ObjectId? folder)
    {
        InitializeComponent();
        _folder = folder;
        List.ItemsSource = _rows;
        Title = folder is { } id && AppState.Current.Vault?.Snapshot().Get(id) is { } item ? item.Name : "SecureSync";
    }

    private Vault? Vault => _app.Vault;
    private string Query => Search.IsVisible ? (Search.Text ?? "").Trim() : "";

    // ---- Lifecycle and live updates --------------------------------------------------------------

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (Vault is null) return;
        Vault.Changed += OnVaultChanged;
        _app.EngineChanged += OnEngineChanged;
        AttachEngine();
        Refresh();
        UpdateSync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (Vault is not null) Vault.Changed -= OnVaultChanged;
        _app.EngineChanged -= OnEngineChanged;
        if (_engine is not null) _engine.StateChanged -= OnChanged;
        _engine = null;
    }

    private void OnEngineChanged(object? sender, EventArgs e)
    {
        AttachEngine();
        RequestUpdate();
    }

    private void AttachEngine()
    {
        if (_engine is not null) _engine.StateChanged -= OnChanged;
        _engine = _app.Engine;
        if (_engine is not null) _engine.StateChanged += OnChanged;
    }

    private void OnChanged(object? sender, EventArgs e) => RequestUpdate();
    private void OnVaultChanged(object? sender, VaultChange e) => RequestUpdate();

    /// <summary>Collapses bursts of change events (from any thread) into at most ~4 redraws a second. Nothing polls.</summary>
    private void RequestUpdate()
    {
        if (Interlocked.Exchange(ref _updateQueued, 1) == 1) return;
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(250), () =>
        {
            Interlocked.Exchange(ref _updateQueued, 0);
            if (Vault is null) return;
            Refresh();
            UpdateSync();
        });
    }

    private void Refresh()
    {
        var vault = Vault;
        if (vault is null) return;
        var snap = vault.Snapshot();
        if (_folder is { } id && snap.Get(id) is not { IsFolder: true })
        {
            // Deleted or moved away on another device.
            if (Navigation.NavigationStack.LastOrDefault() == this) _ = Navigation.PopAsync();
            return;
        }
        if (_folder is { } current) Title = snap.Get(current)!.Name;

        var query = Query;
        IEnumerable<VaultItem> items = query.Length > 0
            ? snap.Items.Where(i => i.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            : snap.Children(_folder);
        var ordered = items.OrderByDescending(i => i.IsFolder).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        var existing = _rows.ToDictionary(r => r.Id);
        bool sameOrder = ordered.Count == _rows.Count && ordered.Select(i => i.Id).SequenceEqual(_rows.Select(r => r.Id));
        if (sameOrder)
        {
            foreach (var item in ordered) existing[item.Id].Item = item;
        }
        else
        {
            _rows.Clear();
            foreach (var item in ordered)
            {
                var location = query.Length > 0 ? "in " + string.Join(" › ", snap.PathTo(item.Parent).Select(p => p.Name).Prepend("SecureSync")) : null;
                if (existing.TryGetValue(item.Id, out var row) && row.Location == location) row.Item = item;
                else row = new ItemRow(item, location);
                _rows.Add(row);
            }
        }
        EmptyLabel.Text = query.Length > 0 ? $"No files match “{query}”." : "Nothing here yet. Tap + to upload files or make a folder.";

        foreach (var row in _rows)
        {
            var item = row.Item;
            string where = row.Location is null ? "" : $" · {row.Location}";
            if (item.IsFolder)
            {
                int count = snap.Children(item.Id).Count;
                row.SetStatus((count == 1 ? "1 item" : $"{count} items") + where, null);
            }
            else if (item.IsAvailable || item.Blob is not { } blob)
            {
                row.SetStatus($"{ItemRow.FormatSize(item.Size)} · {ItemRow.FormatDate(item.ModifiedUtc)}{where}", "status_ok.png");
            }
            else
            {
                var state = _engine?.GetFileState(blob) ?? new FileSyncInfo(FileSyncState.Waiting, 0, "Waiting to sync");
                var icon = state.State switch
                {
                    FileSyncState.Downloading => "status_download.png",
                    FileSyncState.Problem => "status_problem.png",
                    _ => "status_wait.png",
                };
                row.SetStatus($"{ItemRow.FormatSize(item.Size)} · {state.Message}", icon);
            }
        }
    }

    private void UpdateSync()
    {
        if (_workText is not null)
        {
            SyncIcon.Source = "ic_upload.png";
            SyncLabel.Text = _workText;
            return;
        }
        if (_engine is null)
        {
            SyncIcon.Source = "status_wait.png";
            SyncLabel.Text = "Not syncing";
            SyncProgress.IsVisible = false;
            return;
        }
        var status = _engine.GetStatus();
        var names = status.Peers.Count == 1 ? status.Peers[0].DeviceName : $"{status.Peers.Count} devices";
        (SyncIcon.Source, SyncLabel.Text) = status.State switch
        {
            SyncState.Searching => ("status_wait.png", "Looking for your other devices…"),
            SyncState.Syncing => ("ic_sync.png", status.Progress.IsActive
                ? $"Syncing with {names} · {status.Progress.Fraction:P0} · {status.Progress.FilesRemaining} left"
                : $"Syncing with {names}…"),
            SyncState.Synced when status.Problems.Count > 0 => ("status_problem.png", $"Synced with {names} · {status.Problems.Count} need attention"),
            SyncState.Synced => ("status_ok.png", $"Synced with {names}"),
            _ => ("status_wait.png", status.Mode == SyncMode.Paused ? "Sync is off on this phone" : "Not syncing"),
        };
        SyncProgress.IsVisible = status.Progress.IsActive;
        SyncProgress.Progress = status.Progress.Fraction;
    }

    // ---- Browsing --------------------------------------------------------------------------------

    private void OnToggleSearch(object? sender, EventArgs e)
    {
        Search.IsVisible = !Search.IsVisible;
        if (Search.IsVisible) Search.Focus();
        else Search.Text = "";
        Refresh();
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => Refresh();

    private async void OnPullToRefresh(object? sender, EventArgs e)
    {
        // A manual "sync now": look for devices and compare everything again.
        _engine?.RetryNow();
        await Task.Delay(800);
        Refresher.IsRefreshing = false;
    }

    private static ItemRow? RowOf(object? sender) => (sender as BindableObject)?.BindingContext as ItemRow;

    private async void OnItemTapped(object? sender, TappedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        if (row.IsFolder) await Navigation.PushAsync(new FilesPage(row.Id));
        else await OpenAsync(row.Item);
    }

    private async void OnItemMenu(object? sender, EventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        var actions = row.IsFolder
            ? new[] { "Open", "Rename", "Move", "Delete" }
            : new[] { "Open", "Share or save a copy", "Rename", "Move", "Delete" };
        if (row.Location is not null) actions = [.. actions, "Show in folder"];
        switch (await DisplayActionSheetAsync(row.Name, "Cancel", null, actions))
        {
            case "Open" when row.IsFolder: await Navigation.PushAsync(new FilesPage(row.Id)); break;
            case "Open": await OpenAsync(row.Item); break;
            case "Share or save a copy": await ShareAsync(row.Item); break;
            case "Rename": await RenameAsync(row.Item); break;
            case "Move": await MoveAsync(row.Item); break;
            case "Delete": await DeleteAsync(row.Item); break;
            case "Show in folder": await Navigation.PushAsync(new FilesPage(row.Item.Parent)); break;
        }
    }

    // ---- File actions ----------------------------------------------------------------------------

    private async Task<string?> DecryptAsync(VaultItem item)
    {
        if (!item.IsAvailable)
        {
            await DisplayAlertAsync(item.Name, "This file is still syncing to this phone.", "OK");
            return null;
        }
        try
        {
            return await Task.Run(() => Vault!.DecryptToTemp(item.Id));
        }
        catch (Exception ex) when (ex is VaultException or IOException)
        {
            await DisplayAlertAsync("Couldn't open the file", ex.Message, "OK");
            return null;
        }
    }

    private async Task OpenAsync(VaultItem item)
    {
        if (await DecryptAsync(item) is not { } path) return;
        try
        {
            await Launcher.Default.OpenAsync(new OpenFileRequest(item.Name, new ReadOnlyFile(path)));
        }
        catch (Exception)
        {
            await DisplayAlertAsync(item.Name, "There's no app on this phone that can open this kind of file. Use Share to send it to one.", "OK");
        }
    }

    private async Task ShareAsync(VaultItem item)
    {
        if (await DecryptAsync(item) is not { } path) return;
        await Share.Default.RequestAsync(new ShareFileRequest(item.Name, new ShareFile(path)));
    }

    private async Task RenameAsync(VaultItem item)
    {
        var name = await DisplayPromptAsync("Rename", null, "Rename", "Cancel", initialValue: item.Name, maxLength: PathSafety.MaxNameLength);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == item.Name) return;
        await RunAsync(() => Vault!.Rename(item.Id, name.Trim()));
    }

    private async Task MoveAsync(VaultItem item)
    {
        var picked = await FolderPickerPage.PickAsync(Navigation, Vault!.Snapshot(), item.Id, item.Parent);
        if (picked is { } destination) await RunAsync(() => Vault!.Move([item.Id], destination.Folder));
    }

    private async Task DeleteAsync(VaultItem item)
    {
        var what = item.IsFolder ? $"the folder “{item.Name}” and everything in it" : $"“{item.Name}”";
        if (!await DisplayAlertAsync("Delete?", $"Delete {what}? It will be deleted on all your devices.", "Delete", "Cancel")) return;
        await RunAsync(() => Vault!.Delete([item.Id]));
    }

    private async Task RunAsync(Action action)
    {
        try
        {
            await Task.Run(action);
        }
        catch (VaultException ex)
        {
            await DisplayAlertAsync("Couldn't do that", ex.Message, "OK");
        }
    }

    // ---- Adding ----------------------------------------------------------------------------------

    private async void OnAdd(object? sender, EventArgs e)
    {
        if (_workText is not null) return;
        switch (await DisplayActionSheetAsync("Add to this folder", "Cancel", null, "Upload files", "New folder"))
        {
            case "Upload files": await UploadAsync(); break;
            case "New folder": await NewFolderAsync(); break;
        }
    }

    private async Task NewFolderAsync()
    {
        var name = await DisplayPromptAsync("New folder", null, "Create", "Cancel", initialValue: "New folder", maxLength: PathSafety.MaxNameLength);
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunAsync(() => Vault!.CreateFolder(_folder, name.Trim()));
    }

    private async Task UploadAsync()
    {
        IEnumerable<FileResult?>? picked;
        try
        {
            picked = await FilePicker.Default.PickMultipleAsync(new PickOptions { PickerTitle = "Choose files to encrypt and add" });
        }
        catch (Exception)
        {
            return;
        }
        var files = picked?.OfType<FileResult>().ToList() ?? [];
        if (files.Count == 0) return;

        var snap = Vault!.Snapshot();
        int clashes = files.Count(f => snap.FindChild(_folder, PathSafety.SanitizeSegment(f.FileName)) is { IsFolder: false });
        string mode = "Keep both";
        if (clashes > 0)
        {
            mode = await DisplayActionSheetAsync(clashes == 1 ? "A file with that name is already here" : $"{clashes} files with these names are already here",
                "Cancel", null, "Replace", "Keep both", "Skip");
            if (mode is null or "Cancel") return;
        }

        int added = 0;
        var failures = new List<string>();
        AddButton.IsEnabled = false;
        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                var name = PathSafety.SanitizeSegment(files[i].FileName);
                _workText = files.Count == 1 ? $"Encrypting {name}…" : $"Encrypting {name} ({i + 1} of {files.Count})…";
                UpdateSync();
                try
                {
                    var existing = Vault!.Snapshot().FindChild(_folder, name);
                    ObjectId? replace = null;
                    if (existing is not null)
                    {
                        if (mode == "Skip" && !existing.IsFolder) continue;
                        if (mode == "Replace" && !existing.IsFolder) replace = existing.Id;
                        else name = PathSafety.MakeUnique(name, n => Vault!.Snapshot().FindChild(_folder, n) is not null);
                    }
                    await using var stream = await files[i].OpenReadAsync();
                    var target = _folder;
                    await Task.Run(() => Vault!.AddFile(stream, name, DateTime.UtcNow, target, replace));
                    added++;
                }
                catch (Exception ex) when (ex is IOException or VaultException or UnauthorizedAccessException)
                {
                    failures.Add($"{name}: {ex.Message}");
                }
            }
        }
        finally
        {
            _workText = null;
            AddButton.IsEnabled = true;
            UpdateSync();
        }
        if (failures.Count > 0)
            await DisplayAlertAsync("Some files weren't added", string.Join("\n", failures.Take(5)), "OK");
    }

    // ---- Menu ------------------------------------------------------------------------------------

    private async void OnSyncActivity(object? sender, EventArgs e) => await Navigation.PushAsync(new SyncPage());

    private async void OnShowKey(object? sender, EventArgs e)
    {
        if (_app.LoadProfile() is not { } profile) return;
        try
        {
            using var key = await KeyProtection.UnsealAsync(profile, _app.Signer); // asks for fingerprint/screen lock again
            await Navigation.PushAsync(new KeyPage(key.Reveal(), key.Fingerprint));
        }
        catch (KeyStoreCanceledException) { }
        catch (KeyStoreException ex)
        {
            await DisplayAlertAsync("Couldn't show the key", ex.Message, "OK");
        }
    }

    private async void OnLock(object? sender, EventArgs e) => await _app.LockAsync();
}
