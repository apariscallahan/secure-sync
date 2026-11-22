using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using SecureSync.Core.Security;
using SecureSync.Core.Storage;
using SecureSync.Core.Sync;

namespace SecureSync.App.Views;

public partial class ExplorerView : UserControl
{
    private enum NoticeKind { Info, Success, Warning, Error }
    private const string DragFormat = "SecureSync.Items";

    private readonly MainWindow _host;
    private readonly Vault _vault;
    private readonly SyncEngine _engine;
    private readonly ObservableCollection<FileRow> _rows = [];
    private readonly ObservableCollection<FolderNode> _treeRoots = [];
    private readonly FolderNode _rootNode;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _noticeTimer;
    private readonly Storyboard _spin;
    private readonly Stack<ObjectId?> _back = new();
    private readonly Stack<ObjectId?> _forward = new();
    private readonly object _progressGate = new();

    private VaultSnapshot _snapshot;
    private ObjectId? _folder;
    private List<ObjectId> _folderPath = [];
    private string? _lastViewKey;
    private string _sortKey = "name";
    private bool _sortDescending;
    private int _filesDirty = 1;
    private int _statusDirty = 1;
    private int _updateQueued;
    private HashSet<ObjectId> _cut = [];
    private CancellationTokenSource? _workCts;
    private string? _progressText;
    private double _progressFraction;
    private SyncState? _shownState;
    private int _shownProblems;
    private bool _suppressTreeNavigation;
    private bool _suppressSearch;
    private Action? _noticeAction;
    private bool _toldAboutTempCopies;
    private SyncActivityWindow? _activityWindow;

    private Point _dragStart;
    private FileRow? _pressedRow;
    private bool _deferredSelect;
    private FileRow? _dropRow;
    private FolderNode? _dropNode;

    public ExplorerView(MainWindow host, Vault vault, SyncEngine engine)
    {
        InitializeComponent();
        _host = host;
        _vault = vault;
        _engine = engine;
        _snapshot = vault.Snapshot();

        FileList.ItemsSource = _rows;
        TextSearch.SetTextPath(FileList, "Name");
        _rootNode = new FolderNode(null, "SecureSync", (ImageSource)FindResource("LogoImage")) { IsExpanded = true, IsSelected = true };
        _treeRoots.Add(_rootNode);
        FolderTree.DataContext = _treeRoots;
        ChainText.Text = $"Sync chain {vault.Fingerprint} · {vault.DeviceName}";
        _spin = (Storyboard)Resources["SpinStoryboard"];

        _vault.Changed += OnVaultChanged;
        _engine.StateChanged += OnEngineStateChanged;
        _engine.Notice += OnEngineNotice;

        // Not a polling timer: it only runs (once) after something changed, which also caps redraws at ~5 per second.
        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += OnTick;
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _noticeTimer.Tick += (_, _) => HideNotice();

        PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.XButton1) GoBack();
            if (e.ChangedButton == MouseButton.XButton2) GoForward();
        };
        Loaded += (_, _) =>
        {
            Refresh();
            UpdateSyncStatus();
            FileList.Focus();
            if (_vault.UnreadableObjects > 0)
                ShowNotice($"{_vault.UnreadableObjects} damaged record(s) were skipped. They'll be replaced with good copies when this computer syncs.", NoticeKind.Warning);
            else if (!_engine.DiscoveryAvailable)
                ShowNotice("Automatic discovery isn't available on this network. Use Sync › Connect by IP address.", NoticeKind.Warning);
        };
    }

    public bool IsBusy => _workCts is not null;

    /// <summary>Unhooks from the vault and engine before they're disposed (on lock or exit).</summary>
    public void Detach()
    {
        _workCts?.Cancel();
        _timer.Stop();
        _noticeTimer.Stop();
        _activityWindow?.Close();
        _vault.Changed -= OnVaultChanged;
        _engine.StateChanged -= OnEngineStateChanged;
        _engine.Notice -= OnEngineNotice;
    }

    // ---- Background events -----------------------------------------------------------------------

    private void OnVaultChanged(object? sender, VaultChange e)
    {
        Interlocked.Exchange(ref _filesDirty, 1);
        RequestUpdate();
    }

    private void OnEngineStateChanged(object? sender, EventArgs e)
    {
        Interlocked.Exchange(ref _statusDirty, 1);
        RequestUpdate();
    }

    /// <summary>Schedules one UI refresh shortly (from any thread). Many requests in a burst collapse into one.</summary>
    private void RequestUpdate()
    {
        if (Interlocked.Exchange(ref _updateQueued, 1) == 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { if (!_timer.IsEnabled) _timer.Start(); });
    }
    private void OnEngineNotice(object? sender, SyncNotice n) =>
        Dispatcher.BeginInvoke(() => ShowNotice(n.Message, n.IsError ? NoticeKind.Error : NoticeKind.Info));

    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        Interlocked.Exchange(ref _updateQueued, 0);
        if (Interlocked.Exchange(ref _filesDirty, 0) == 1) Refresh();
        if (Interlocked.Exchange(ref _statusDirty, 0) == 1)
        {
            UpdateSyncStatus();
            UpdateRowStatuses();
        }
        UpdateProgress();
    }

    // ---- Listing ---------------------------------------------------------------------------------

    private string Query => SearchBox?.Text.Trim() ?? ""; // null while the XAML is still loading

    private List<FileRow> SelectedRows => FileList.SelectedItems.Cast<FileRow>().ToList();

    private void Refresh()
    {
        _snapshot = _vault.Snapshot();

        // The open folder may have been deleted or moved away on another computer: step up to the nearest that remains.
        if (_folder is { } current && _snapshot.Get(current) is not { IsFolder: true })
        {
            var fallback = _folderPath.LastOrDefault(id => _snapshot.Get(id) is { IsFolder: true });
            _folder = fallback == default ? null : fallback;
        }
        _folderPath = _snapshot.PathTo(_folder).Select(f => f.Id).ToList();

        SyncTree();
        RebuildRows();
        UpdateBreadcrumb();
        UpdateCounts();
        UpdateRowStatuses();
        var window = Window.GetWindow(this);
        if (window is not null)
            window.Title = _folder is { } id && _snapshot.Get(id) is { } folder ? $"{folder.Name} — SecureSync" : $"SecureSync — {_vault.DeviceName}";
        CommandManager.InvalidateRequerySuggested();
    }

    private void RebuildRows()
    {
        var query = Query;
        bool searching = query.Length > 0;
        var viewKey = searching ? "?" + query : "/" + _folder;
        bool reset = viewKey != _lastViewKey;
        _lastViewKey = viewKey;

        var items = searching
            ? _snapshot.Items.Where(i => i.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            : _snapshot.Children(_folder);
        var existing = reset ? [] : _rows.ToDictionary(r => r.Id);
        var rows = new List<FileRow>();
        foreach (var item in items)
        {
            if (existing.TryGetValue(item.Id, out var row)) row.Item = item;
            else row = new FileRow(item, searching ? LocationOf(item) : null) { IsCut = _cut.Contains(item.Id) };
            rows.Add(row);
        }
        rows.Sort(CompareRows);

        var selected = reset ? [] : FileList.SelectedItems.Cast<FileRow>().Select(r => r.Id).ToHashSet();
        if (reset)
        {
            _rows.Clear();
            foreach (var row in rows) _rows.Add(row);
        }
        else
        {
            var wanted = rows.Select(r => r.Id).ToHashSet();
            for (int i = _rows.Count - 1; i >= 0; i--)
                if (!wanted.Contains(_rows[i].Id)) _rows.RemoveAt(i);
            for (int i = 0; i < rows.Count; i++)
            {
                if (i < _rows.Count && ReferenceEquals(_rows[i], rows[i])) continue;
                int at = _rows.IndexOf(rows[i]);
                if (at >= 0) _rows.Move(at, i);
                else _rows.Insert(i, rows[i]);
            }
            foreach (var row in _rows)
                if (selected.Contains(row.Id) && !FileList.SelectedItems.Contains(row)) FileList.SelectedItems.Add(row);
        }

        EmptyText.Text = searching ? $"No items match “{query}”." : "This folder is empty.\nUpload files, or drag them here from File Explorer.";
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private string LocationOf(VaultItem item)
    {
        var path = _snapshot.PathTo(item.Parent).Select(p => p.Name).Prepend("SecureSync");
        return "in " + string.Join(" › ", path);
    }

    private int CompareRows(FileRow a, FileRow b)
    {
        if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;
        int result = _sortKey switch
        {
            "date" => a.Modified.CompareTo(b.Modified),
            "type" => string.Compare(a.TypeName, b.TypeName, StringComparison.CurrentCultureIgnoreCase),
            "size" => a.Item.Size.CompareTo(b.Item.Size),
            "status" => string.Compare(a.StatusText, b.StatusText, StringComparison.CurrentCultureIgnoreCase),
            _ => 0,
        };
        if (result == 0) result = Shell.NaturalOrder.Compare(a.Name, b.Name);
        return _sortDescending ? -result : result;
    }

    private void SyncTree()
    {
        var icon = Shell.ForFolder().Icon;
        void SyncChildren(FolderNode node, ObjectId? id)
        {
            var folders = _snapshot.Children(id).Where(i => i.IsFolder).OrderBy(i => i.Name, Shell.NaturalOrder).ToList();
            var keep = folders.Select(f => f.Id).ToHashSet();
            for (int i = node.Children.Count - 1; i >= 0; i--)
                if (!keep.Contains(node.Children[i].Id!.Value)) node.Children.RemoveAt(i);
            for (int i = 0; i < folders.Count; i++)
            {
                var folder = folders[i];
                int at = -1;
                for (int j = 0; j < node.Children.Count; j++)
                    if (node.Children[j].Id == folder.Id) { at = j; break; }
                if (at < 0) node.Children.Insert(i, new FolderNode(folder.Id, folder.Name, icon));
                else
                {
                    if (at != i) node.Children.Move(at, i);
                    node.Children[i].Name = folder.Name;
                }
                SyncChildren(node.Children[i], folder.Id);
            }
        }
        SyncChildren(_rootNode, null);
        SelectTreeNode(_folder);
    }

    private void SelectTreeNode(ObjectId? folder)
    {
        _suppressTreeNavigation = true;
        try
        {
            var node = _rootNode;
            foreach (var id in _folderPath)
            {
                node.IsExpanded = true;
                var next = node.Children.FirstOrDefault(c => c.Id == id);
                if (next is null) break;
                node = next;
            }
            if (!node.IsSelected) node.IsSelected = true;
        }
        finally
        {
            _suppressTreeNavigation = false;
        }
    }

    private void UpdateBreadcrumb()
    {
        Breadcrumb.Children.Clear();
        Breadcrumb.Children.Add(new Image { Source = (ImageSource)FindResource("LogoImage"), Width = 16, Height = 16, Margin = new Thickness(2, 0, 4, 0) });
        AddCrumb("SecureSync", null);
        if (Query.Length > 0)
        {
            AddChevron();
            Breadcrumb.Children.Add(new TextBlock { Text = $"Search results for “{Query}”", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
            return;
        }
        foreach (var folder in _snapshot.PathTo(_folder))
        {
            AddChevron();
            AddCrumb(folder.Name, folder.Id);
        }
    }

    private void AddCrumb(string text, ObjectId? folder)
    {
        var button = new Button
        {
            Style = (Style)FindResource("ToolButton"),
            Content = text,
            Height = 24,
            Padding = new Thickness(5, 0, 5, 0),
            Margin = new Thickness(0),
            Focusable = false,
        };
        button.Click += (_, _) => NavigateTo(folder);
        Breadcrumb.Children.Add(button);
    }

    private void AddChevron() => Breadcrumb.Children.Add(new TextBlock
    {
        Style = (Style)FindResource("Icon"),
        Text = "",
        FontSize = 8,
        Foreground = (Brush)FindResource("MutedBrush"),
        Margin = new Thickness(2, 1, 2, 0),
    });

    private void UpdateCounts()
    {
        ItemCountText.Text = _rows.Count == 1 ? "1 item" : $"{_rows.Count:N0} items";
        var selected = SelectedRows;
        SelectionSeparator.Visibility = SelectionText.Visibility = selected.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        long bytes = selected.Where(r => !r.IsFolder).Sum(r => r.Item.Size);
        SelectionText.Text = (selected.Count == 1 ? "1 item selected" : $"{selected.Count:N0} items selected")
                             + (bytes > 0 && selected.All(r => !r.IsFolder) ? $"  {FileRow.FormatBytes(bytes)}" : "");
    }

    private void UpdateRowStatuses()
    {
        var available = (Brush)FindResource("SuccessBrush");
        var accent = (Brush)FindResource("AccentBrush");
        var muted = (Brush)FindResource("MutedBrush");
        var problem = (Brush)FindResource("DangerBrush");
        foreach (var row in _rows)
        {
            if (row.IsFolder)
            {
                row.SetStatus("", null, "");
                continue;
            }
            if (row.Item.IsAvailable || row.Item.Blob is not { } blob)
            {
                row.SetStatus("", available, "On this computer");
                continue;
            }
            var info = _engine.GetFileState(blob);
            switch (info.State)
            {
                case FileSyncState.Downloading: row.SetStatus("", accent, info.Message); break;
                case FileSyncState.Problem: row.SetStatus("", problem, info.Message); break;
                case FileSyncState.Queued: row.SetStatus("", accent, info.Message); break;
                default: row.SetStatus("", muted, info.Message); break;
            }
        }
    }

    // ---- Navigation ------------------------------------------------------------------------------

    private void NavigateTo(ObjectId? folder, bool record = true)
    {
        if (record && folder != _folder)
        {
            _back.Push(_folder);
            _forward.Clear();
        }
        _folder = folder;
        _folderPath = _snapshot.PathTo(folder).Select(f => f.Id).ToList();
        if (SearchBox.Text.Length > 0)
        {
            _suppressSearch = true;
            SearchBox.Text = "";
            _suppressSearch = false;
        }
        Refresh();
        FileList.Focus();
    }

    private void GoBack()
    {
        if (_back.Count == 0) return;
        _forward.Push(_folder);
        NavigateTo(_back.Pop(), record: false);
    }

    private void GoForward()
    {
        if (_forward.Count == 0) return;
        _back.Push(_folder);
        NavigateTo(_forward.Pop(), record: false);
    }

    private void OnBack(object sender, ExecutedRoutedEventArgs e) => GoBack();
    private void OnForward(object sender, ExecutedRoutedEventArgs e) => GoForward();
    private void OnUp(object sender, ExecutedRoutedEventArgs e)
    {
        if (Query.Length > 0) NavigateTo(_folder);
        else if (_folder is { } id) NavigateTo(_snapshot.Get(id)?.Parent);
    }

    private void CanBack(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _back.Count > 0;
    private void CanForward(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _forward.Count > 0;
    private void CanUp(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _folder is not null || Query.Length > 0;

    private void OnRefresh(object sender, ExecutedRoutedEventArgs e)
    {
        _lastViewKey = null;
        Refresh();
    }

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressTreeNavigation || e.NewValue is not FolderNode node || node.Id == _folder && Query.Length == 0) return;
        NavigateTo(node.Id);
        FolderTree.Focus();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSearch) return;
        Refresh();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SearchBox.Text = "";
            FileList.Focus();
        }
        else if (e.Key is Key.Enter or Key.Down)
        {
            FileList.Focus();
            if (_rows.Count > 0) FileList.SelectedIndex = 0;
        }
    }

    private void OnFocusSearch(object sender, ExecutedRoutedEventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Tag: string key }) SortBy(key, toggle: true);
    }

    private void OnSortMenu(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string key }) SortBy(key, toggle: false);
    }

    private void SortBy(string key, bool toggle)
    {
        _sortDescending = toggle && _sortKey == key && !_sortDescending;
        _sortKey = key;
        foreach (var column in ((GridView)FileList.View).Columns)
        {
            if (column.Header is not GridViewColumnHeader header) continue;
            var title = ((string)header.Content).TrimEnd(' ', '▲', '▼');
            header.Content = (string)header.Tag == key ? $"{title} {(_sortDescending ? "▼" : "▲")}" : title;
        }
        foreach (var item in new[] { SortName, SortStatus, SortDate, SortType, SortSize })
            item.IsChecked = (string)item.Tag == key;
        _lastViewKey = null;
        Refresh();
    }

    private void OnListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var view = (GridView)FileList.View;
        double others = view.Columns.Where(c => c != NameColumn).Sum(c => c.ActualWidth);
        NameColumn.Width = Math.Max(200, FileList.ActualWidth - others - 30);
    }

    private void OnToggleTree(object sender, RoutedEventArgs e)
    {
        bool show = ShowTreeMenu.IsChecked;
        TreeColumn.MinWidth = show ? 120 : 0;
        TreeColumn.Width = show ? new GridLength(220) : new GridLength(0);
        Splitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Selection, keyboard and mouse -----------------------------------------------------------

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCounts();

    private void OnSelectAll(object sender, ExecutedRoutedEventArgs e)
    {
        FileList.SelectAll();
        FileList.Focus();
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        switch (e.Key)
        {
            case Key.Enter when SelectedRows is [var row]:
                OpenRow(row);
                e.Handled = true;
                break;
            case Key.Back:
                OnUp(sender, null!);
                e.Handled = true;
                break;
            case Key.Escape when _cut.Count > 0:
                ClearCut();
                e.Handled = true;
                break;
        }
    }

    private FileRow? RowAt(DependencyObject? source) =>
        source is null ? null : ItemsControl.ContainerFromElement(FileList, source) is ListViewItem { Content: FileRow row } ? row : null;

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<TextBox>(d) is not null) return; // rename box
        if (FindParent<GridViewColumnHeader>((DependencyObject)e.OriginalSource) is not null) return;
        var row = RowAt(e.OriginalSource as DependencyObject);
        _pressedRow = row;
        _dragStart = e.GetPosition(FileList);
        _deferredSelect = false;
        if (row is null) return;
        if (e.ClickCount == 2)
        {
            OpenRow(row);
            e.Handled = true;
            return;
        }
        // Keep a multi-selection intact when pressing on one of its items, so it can be dragged as a group.
        if (FileList.SelectedItems.Contains(row) && Keyboard.Modifiers == ModifierKeys.None && FileList.SelectedItems.Count > 1)
        {
            _deferredSelect = true;
            e.Handled = true;
            FileList.Focus();
        }
    }

    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferredSelect && _pressedRow is not null)
        {
            FileList.SelectedItems.Clear();
            FileList.SelectedItem = _pressedRow;
        }
        _deferredSelect = false;
        _pressedRow = null;
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressedRow is null || IsBusy) return;
        var delta = e.GetPosition(FileList) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        if (!FileList.SelectedItems.Contains(_pressedRow))
        {
            FileList.SelectedItems.Clear();
            FileList.SelectedItem = _pressedRow;
        }
        var ids = SelectedRows.Select(r => r.Id.ToString()).ToArray();
        _pressedRow = null;
        _deferredSelect = false;
        var data = new DataObject(DragFormat, ids);
        DragDrop.DoDragDrop(FileList, data, DragDropEffects.Move);
        ClearDropHighlight();
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null and not T)
            child = child is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child);
        return child as T;
    }

    private void OnListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var row = RowAt(e.OriginalSource as DependencyObject);
        if (row is null)
        {
            FileList.SelectedItems.Clear();
        }
        else if (!FileList.SelectedItems.Contains(row))
        {
            FileList.SelectedItems.Clear();
            FileList.SelectedItem = row;
        }
        bool onItems = row is not null;
        bool single = SelectedRows.Count == 1;
        foreach (var item in new FrameworkElement[] { MenuSaveCopy, MenuSeparator1, MenuCut, MenuMoveTo, MenuSeparator2, MenuRename, MenuDelete })
            item.Visibility = onItems ? Visibility.Visible : Visibility.Collapsed;
        MenuOpen.Visibility = onItems && single ? Visibility.Visible : Visibility.Collapsed;
        MenuOpen.Header = SelectedRows is [{ IsFolder: true }] ? "_Open" : "_Open";
        MenuOpenLocation.Visibility = onItems && single && Query.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        MenuSeparator3.Visibility = MenuNewFolder.Visibility = MenuUploadHere.Visibility = onItems ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- Open ------------------------------------------------------------------------------------

    private int SelectedCount => FileList?.SelectedItems.Count ?? 0;

    private void CanActOnSingle(object sender, CanExecuteRoutedEventArgs e) =>
        e.CanExecute = SelectedCount == 1 && !IsEditing();

    private void CanActOnSelection(object sender, CanExecuteRoutedEventArgs e) =>
        e.CanExecute = SelectedCount > 0 && !IsEditing() && !IsBusy;

    private bool IsEditing() => _rows.Any(r => r.IsEditing);

    private void OnOpen(object sender, ExecutedRoutedEventArgs e)
    {
        if (SelectedRows is [var row]) OpenRow(row);
    }

    private async void OpenRow(FileRow row)
    {
        if (row.IsFolder)
        {
            NavigateTo(row.Id);
            return;
        }
        if (!row.Item.IsAvailable)
        {
            ShowNotice($"“{row.Name}” is still syncing to this computer. {row.StatusText}.", NoticeKind.Info);
            return;
        }
        try
        {
            var path = await Task.Run(() => _vault.DecryptToTemp(row.Id));
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            if (!_toldAboutTempCopies)
            {
                _toldAboutTempCopies = true;
                ShowNotice("Opened a temporary decrypted copy. It's deleted when you lock or close SecureSync. Changes to it aren't saved back — upload the edited file to keep them.", NoticeKind.Info);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultException or Win32Exception)
        {
            ShowNotice($"Couldn't open “{row.Name}”: {ex.Message}", NoticeKind.Error);
        }
    }

    private void OnOpenLocation(object sender, RoutedEventArgs e)
    {
        if (SelectedRows is not [var row]) return;
        var id = row.Id;
        NavigateTo(row.Item.Parent);
        var target = _rows.FirstOrDefault(r => r.Id == id);
        if (target is null) return;
        FileList.SelectedItem = target;
        FileList.ScrollIntoView(target);
    }

    // ---- New folder and rename -------------------------------------------------------------------

    private void CanUseFolder(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = !IsEditing();

    private void OnNewFolder(object sender, ExecutedRoutedEventArgs e)
    {
        if (Query.Length > 0) NavigateTo(_folder);
        try
        {
            var name = PathSafety.MakeUnique("New folder", n => _snapshot.FindChild(_folder, n) is not null, isFolder: true);
            var folder = _vault.CreateFolder(_folder, name);
            Interlocked.Exchange(ref _filesDirty, 0);
            Refresh();
            if (_rows.FirstOrDefault(r => r.Id == folder.Id) is { } row) BeginRename(row);
        }
        catch (VaultException ex)
        {
            ShowNotice(ex.Message, NoticeKind.Error);
        }
    }

    private void OnRename(object sender, ExecutedRoutedEventArgs e)
    {
        if (SelectedRows is [var row]) BeginRename(row);
    }

    private void BeginRename(FileRow row)
    {
        FileList.SelectedItems.Clear();
        FileList.SelectedItem = row;
        FileList.ScrollIntoView(row);
        row.EditName = row.Name;
        row.IsEditing = true;
    }

    private void OnRenameBoxVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox { DataContext: FileRow row } box || e.NewValue is not true) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            box.Focus();
            int stem = row.IsFolder ? row.Name.Length : Path.GetFileNameWithoutExtension(row.Name).Length;
            box.Select(0, stem > 0 ? stem : row.Name.Length);
        });
    }

    private void OnRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: FileRow row }) return;
        if (e.Key == Key.Enter)
        {
            CommitRename(row);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            row.IsEditing = false;
            FileList.Focus();
            e.Handled = true;
        }
    }

    private void OnRenameLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: FileRow { IsEditing: true } row }) CommitRename(row);
    }

    private void CommitRename(FileRow row)
    {
        if (!row.IsEditing) return;
        row.IsEditing = false;
        var name = row.EditName.Trim();
        if (name.Length > 0 && name != row.Name)
        {
            try
            {
                _vault.Rename(row.Id, name);
            }
            catch (VaultException ex)
            {
                ShowNotice(ex.Message, NoticeKind.Error);
            }
        }
        if (FileList.IsKeyboardFocusWithin || !IsKeyboardFocusWithin) FileList.Focus();
        if (FileList.ItemContainerGenerator.ContainerFromItem(row) is ListViewItem container) container.Focus();
    }

    // ---- Delete, cut/paste, move -----------------------------------------------------------------

    private async void OnDelete(object sender, ExecutedRoutedEventArgs e)
    {
        var rows = SelectedRows;
        if (rows.Count == 0) return;
        int inside = rows.Where(r => r.IsFolder).Sum(r => _snapshot.Descendants(r.Id).Count());
        string what = rows switch
        {
            [{ IsFolder: false } r] => $"Delete “{r.Name}”?",
            [{ IsFolder: true } r] => $"Delete the folder “{r.Name}”{(inside > 0 ? $" and the {inside:N0} items in it" : "")}?",
            _ => $"Delete these {rows.Count:N0} items{(inside > 0 ? $" (and {inside:N0} items inside folders)" : "")}?",
        };
        var answer = MessageBox.Show(Window.GetWindow(this),
            what + "\n\nThey'll be deleted on every computer that syncs this chain. Original files elsewhere on your computer aren't affected.",
            "Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        var ids = rows.Select(r => r.Id).ToList();
        try
        {
            await Task.Run(() => _vault.Delete(ids));
        }
        catch (Exception ex) when (ex is VaultException or IOException)
        {
            ShowNotice(ex.Message, NoticeKind.Error);
        }
    }

    private void OnCut(object sender, ExecutedRoutedEventArgs e)
    {
        ClearCut();
        _cut = SelectedRows.Select(r => r.Id).ToHashSet();
        foreach (var row in _rows) row.IsCut = _cut.Contains(row.Id);
        ShowNotice($"{(_cut.Count == 1 ? "1 item" : $"{_cut.Count:N0} items")} cut. Open the destination folder and choose Paste (Ctrl+V).", NoticeKind.Info);
    }

    private void ClearCut()
    {
        _cut.Clear();
        foreach (var row in _rows) row.IsCut = false;
    }

    private void CanPaste(object sender, CanExecuteRoutedEventArgs e) =>
        e.CanExecute = _cut.Count > 0 && Query.Length == 0 && !IsEditing();

    private void OnPaste(object sender, ExecutedRoutedEventArgs e)
    {
        var ids = _cut.Where(id => _snapshot.Get(id) is not null).ToList();
        ClearCut();
        MoveItems(ids, _folder);
    }

    private void OnMoveTo(object sender, ExecutedRoutedEventArgs e)
    {
        var rows = SelectedRows;
        var dialog = new MoveToDialog(_snapshot, rows.Select(r => r.Id).ToHashSet(), _folder) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) MoveItems(rows.Select(r => r.Id).ToList(), dialog.Destination);
    }

    private void MoveItems(IReadOnlyList<ObjectId> ids, ObjectId? target)
    {
        if (ids.Count == 0) return;
        try
        {
            _vault.Move(ids, target);
        }
        catch (VaultException ex)
        {
            ShowNotice(ex.Message, NoticeKind.Error);
        }
    }

    // ---- Drag and drop (inside the app: move; from File Explorer: upload) ------------------------

    private static ObjectId[]? DraggedItems(IDataObject data) =>
        data.GetDataPresent(DragFormat) && data.GetData(DragFormat) is string[] ids
            ? ids.Select(s => ObjectId.TryParse(s, out var id) ? id : default).Where(id => id != default).ToArray()
            : null;

    /// <summary>Whether items can be dropped into <paramref name="target"/> (not into themselves or their own subfolders).</summary>
    private bool CanMoveInto(ObjectId[] items, ObjectId? target) =>
        items.All(id => target is not { } t || !_snapshot.IsSelfOrDescendant(t, id))
        && items.Any(id => _snapshot.Get(id)?.Parent != target);

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        var row = RowAt(e.OriginalSource as DependencyObject);
        var targetRow = row is { IsFolder: true } ? row : null;
        ObjectId? target = targetRow?.Id ?? (Query.Length == 0 ? _folder : null);
        e.Effects = DragDropEffects.None;
        if (DraggedItems(e.Data) is { } items)
        {
            if (targetRow is not null && CanMoveInto(items, target)) e.Effects = DragDropEffects.Move;
            else targetRow = null;
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop) && !IsBusy && (targetRow is not null || Query.Length == 0))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            targetRow = null;
        }
        SetDropHighlight(targetRow, null);
        e.Handled = true;
    }

    private void OnListDragLeave(object sender, DragEventArgs e) => ClearDropHighlight();

    private async void OnListDrop(object sender, DragEventArgs e)
    {
        var row = RowAt(e.OriginalSource as DependencyObject);
        ObjectId? target = row is { IsFolder: true } ? row.Id : _folder;
        ClearDropHighlight();
        if (DraggedItems(e.Data) is { } items)
        {
            if (row is { IsFolder: true } && CanMoveInto(items, target)) MoveItems(items, target);
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            await UploadPathsAsync(paths, target);
        }
    }

    private FolderNode? NodeAt(DependencyObject? source) =>
        FindParent<TreeViewItem>(source)?.DataContext as FolderNode;

    private void OnTreeDragOver(object sender, DragEventArgs e)
    {
        var node = NodeAt(e.OriginalSource as DependencyObject);
        e.Effects = DragDropEffects.None;
        if (node is not null)
        {
            if (DraggedItems(e.Data) is { } items && CanMoveInto(items, node.Id)) e.Effects = DragDropEffects.Move;
            else if (e.Data.GetDataPresent(DataFormats.FileDrop) && !IsBusy) e.Effects = DragDropEffects.Copy;
        }
        SetDropHighlight(null, e.Effects == DragDropEffects.None ? null : node);
        e.Handled = true;
    }

    private void OnTreeDragLeave(object sender, DragEventArgs e) => ClearDropHighlight();

    private async void OnTreeDrop(object sender, DragEventArgs e)
    {
        var node = NodeAt(e.OriginalSource as DependencyObject);
        ClearDropHighlight();
        if (node is null) return;
        if (DraggedItems(e.Data) is { } items)
        {
            if (CanMoveInto(items, node.Id)) MoveItems(items, node.Id);
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            await UploadPathsAsync(paths, node.Id);
        }
    }

    private void SetDropHighlight(FileRow? row, FolderNode? node)
    {
        if (_dropRow != row)
        {
            if (_dropRow is not null) _dropRow.IsDropTarget = false;
            _dropRow = row;
            if (row is not null) row.IsDropTarget = true;
        }
        if (_dropNode != node)
        {
            if (_dropNode is not null) _dropNode.IsDropTarget = false;
            _dropNode = node;
            if (node is not null) node.IsDropTarget = true;
        }
    }

    private void ClearDropHighlight() => SetDropHighlight(null, null);

    // ---- Upload ----------------------------------------------------------------------------------

    private void CanUpload(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = !IsBusy;

    private async void OnUploadFiles(object sender, ExecutedRoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "Choose files to encrypt and add" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await UploadPathsAsync(dialog.FileNames, Query.Length == 0 ? _folder : null);
    }

    private async void OnUploadFolder(object sender, ExecutedRoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Multiselect = true, Title = "Choose folders to encrypt and add" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await UploadPathsAsync(dialog.FolderNames, Query.Length == 0 ? _folder : null);
    }

    private bool IsInsideVault(string path)
    {
        var vault = Path.TrimEndingDirectorySeparator(_vault.Folder);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return full.Equals(vault, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(vault + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record UploadItem(string Source, string[] Folders, bool IsEmptyFolder = false);

    private async Task UploadPathsAsync(IReadOnlyList<string> paths, ObjectId? target)
    {
        if (IsBusy)
        {
            ShowNotice("Please wait for the current task to finish.", NoticeKind.Info);
            return;
        }
        int skippedVault = 0;
        SetProgress("Looking for files…", 0);
        var items = await Task.Run(() =>
        {
            var list = new List<UploadItem>();
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            };
            foreach (var path in paths)
            {
                if (IsInsideVault(path)) { skippedVault++; continue; }
                if (File.Exists(path))
                {
                    list.Add(new UploadItem(path, []));
                }
                else if (Directory.Exists(path))
                {
                    var root = Path.TrimEndingDirectorySeparator(path);
                    var baseName = PathSafety.SanitizeSegment(Path.GetFileName(root));
                    list.Add(new UploadItem(root, [baseName], IsEmptyFolder: true)); // makes sure the folder itself exists
                    foreach (var dir in Directory.EnumerateDirectories(root, "*", options))
                        list.Add(new UploadItem(dir, [baseName, .. Segments(Path.GetRelativePath(root, dir))], IsEmptyFolder: true));
                    foreach (var file in Directory.EnumerateFiles(root, "*", options))
                    {
                        if (IsInsideVault(file)) { skippedVault++; continue; }
                        list.Add(new UploadItem(file, [baseName, .. Segments(Path.GetRelativePath(root, Path.GetDirectoryName(file)!))]));
                    }
                }
            }
            return list;
        });
        ClearProgress();
        if (skippedVault > 0) ShowNotice("Skipped SecureSync's own encrypted folder — it can't be uploaded into itself.", NoticeKind.Info);
        if (items.Count == 0) return;

        // Files that would land on an existing file of the same name: ask once what to do.
        var snap = _vault.Snapshot();
        int clashes = items.Count(i => !i.IsEmptyFolder && FindExisting(snap, target, i.Folders, Path.GetFileName(i.Source)) is { IsFolder: false });
        var mode = ClashMode.KeepBoth;
        if (clashes > 0)
        {
            var example = items.First(i => !i.IsEmptyFolder && FindExisting(snap, target, i.Folders, Path.GetFileName(i.Source)) is { IsFolder: false });
            var choice = ChoiceDialog.Show(Window.GetWindow(this), "Replace or skip files",
                clashes == 1 ? $"The destination already has a file named “{Path.GetFileName(example.Source)}”."
                             : $"The destination already has {clashes:N0} files with the same names (e.g. “{Path.GetFileName(example.Source)}”).",
                ["Replace the files in the destination", "Skip these files", "Keep both (the new files get a number added)"]);
            if (choice < 0) return;
            mode = (ClashMode)choice;
        }

        var files = items.Where(i => !i.IsEmptyFolder).ToList();
        long total = Math.Max(1, files.Sum(i => SafeLength(i.Source)));
        int added = 0, skipped = 0;
        var failures = new List<string>();
        bool completed = await RunWorkAsync(ct =>
        {
            var folderIds = new Dictionary<string, ObjectId?>(StringComparer.OrdinalIgnoreCase) { [""] = target };
            ObjectId? EnsureFolders(string[] folders)
            {
                var key = "";
                ObjectId? parent = target;
                foreach (var name in folders)
                {
                    key += "/" + name;
                    if (!folderIds.TryGetValue(key, out var id))
                    {
                        var existing = _vault.Snapshot().FindChild(parent, name);
                        id = existing is { IsFolder: true } ? existing.Id : _vault.CreateFolder(parent,
                            existing is null ? name : PathSafety.MakeUnique(name, n => _vault.Snapshot().FindChild(parent, n) is not null, isFolder: true)).Id;
                        folderIds[key] = id;
                    }
                    parent = id;
                }
                return parent;
            }

            foreach (var folder in items.Where(i => i.IsEmptyFolder)) EnsureFolders(folder.Folders);
            for (int i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var item = files[i];
                var name = PathSafety.SanitizeSegment(Path.GetFileName(item.Source));
                SetProgress(files.Count == 1 ? $"Encrypting {name}" : $"Encrypting {name} ({i + 1:N0} of {files.Count:N0})", null);
                try
                {
                    var parent = EnsureFolders(item.Folders);
                    var existing = _vault.Snapshot().FindChild(parent, name);
                    ObjectId? replace = null;
                    if (existing is not null)
                    {
                        if (existing.IsFolder || mode == ClashMode.KeepBoth)
                            name = PathSafety.MakeUnique(name, n => _vault.Snapshot().FindChild(parent, n) is not null);
                        else if (mode == ClashMode.Skip) { skipped++; AddProgress(SafeLength(item.Source), total); continue; }
                        else replace = existing.Id;
                    }
                    _vault.AddFile(item.Source, parent, name, replace, n => AddProgress(n, total), ct);
                    added++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultException)
                {
                    failures.Add($"{name}: {ex.Message}");
                }
            }
        });

        string summary = $"Encrypted and added {added:N0} {(added == 1 ? "file" : "files")}" + (skipped > 0 ? $", skipped {skipped:N0}" : "") + ".";
        if (failures.Count > 0)
            ShowNotice($"{summary} {failures.Count:N0} couldn't be read: {failures[0]}" + (failures.Count > 1 ? $" (and {failures.Count - 1} more)" : ""), NoticeKind.Error);
        else if (completed)
            ShowNotice(summary, NoticeKind.Success);
        else
            ShowNotice($"Upload canceled. {added:N0} {(added == 1 ? "file was" : "files were")} added before you stopped it.", NoticeKind.Info);
    }

    private enum ClashMode { Replace = 0, Skip = 1, KeepBoth = 2 }

    private static VaultItem? FindExisting(VaultSnapshot snap, ObjectId? target, string[] folders, string name)
    {
        ObjectId? parent = target;
        foreach (var folder in folders)
        {
            if (snap.FindChild(parent, folder) is not { IsFolder: true } existing) return null;
            parent = existing.Id;
        }
        return snap.FindChild(parent, PathSafety.SanitizeSegment(name));
    }

    private static IEnumerable<string> Segments(string relative) =>
        relative == "." ? [] : relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Select(PathSafety.SanitizeSegment);

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch (IOException) { return 0; }
    }

    // ---- Save decrypted copy ---------------------------------------------------------------------

    private async void OnSaveCopy(object sender, ExecutedRoutedEventArgs e)
    {
        var rows = SelectedRows;
        if (rows.Count == 0) return;
        var owner = Window.GetWindow(this);

        if (rows is [{ IsFolder: false } single])
        {
            if (!single.Item.IsAvailable)
            {
                ShowNotice($"“{single.Name}” is still syncing to this computer.", NoticeKind.Info);
                return;
            }
            var dialog = new SaveFileDialog { Title = "Save a decrypted copy", FileName = PathSafety.SanitizeSegment(single.Name), Filter = "All files|*.*" };
            if (dialog.ShowDialog(owner) != true) return;
            var id = single.Id;
            long size = Math.Max(1, single.Item.Size);
            if (await RunWorkAsync(ct =>
                {
                    SetProgress($"Decrypting {single.Name}", 0);
                    _vault.Export(id, dialog.FileName, n => AddProgress(n, size), ct);
                }))
                ShowNotice($"Saved a decrypted copy of “{single.Name}”.", NoticeKind.Success);
            return;
        }

        var folderDialog = new OpenFolderDialog { Title = "Choose where to save the decrypted copies" };
        if (folderDialog.ShowDialog(owner) != true) return;
        var destination = folderDialog.FolderName;

        // Expand folders, keeping their structure (and empty folders) relative to the selection.
        var work = new List<(VaultItem Item, string[] Folders)>();
        foreach (var row in rows)
        {
            if (!row.IsFolder)
            {
                work.Add((row.Item, []));
                continue;
            }
            work.Add((row.Item, []));
            foreach (var d in _snapshot.Descendants(row.Id))
            {
                var path = _snapshot.PathTo(d.Parent).SkipWhile(p => p.Id != row.Id).Select(p => p.Name).ToArray();
                work.Add((d, path));
            }
        }
        long total = Math.Max(1, work.Where(w => !w.Item.IsFolder && w.Item.IsAvailable).Sum(w => w.Item.Size));
        int saved = 0, unavailable = 0;
        bool completed = await RunWorkAsync(ct =>
        {
            foreach (var (item, folders) in work)
            {
                ct.ThrowIfCancellationRequested();
                var target = PathSafety.ToSafeLocalPath(destination, folders, item.Name);
                if (item.IsFolder)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                if (!item.IsAvailable)
                {
                    unavailable++;
                    continue;
                }
                SetProgress($"Decrypting {item.Name} ({saved + 1:N0})", null);
                var dir = Path.GetDirectoryName(target)!;
                Directory.CreateDirectory(dir);
                if (File.Exists(target)) target = Path.Combine(dir, PathSafety.MakeUnique(Path.GetFileName(target), n => File.Exists(Path.Combine(dir, n))));
                _vault.Export(item.Id, target, n => AddProgress(n, total), ct);
                saved++;
            }
        });
        if (saved > 0 || completed)
            ShowNotice($"Saved {saved:N0} decrypted {(saved == 1 ? "file" : "files")} to {destination}." +
                       (unavailable > 0 ? $" {unavailable:N0} still syncing to this computer were skipped." : ""), NoticeKind.Success);
    }

    // ---- Background work + progress --------------------------------------------------------------

    /// <summary>Runs a cancellable job off the UI thread with the status-bar progress showing. Returns false if canceled.</summary>
    private async Task<bool> RunWorkAsync(Action<CancellationToken> work)
    {
        _workCts = new CancellationTokenSource();
        lock (_progressGate) _progressFraction = 0;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var token = _workCts.Token;
            await Task.Run(() => work(token), token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultException)
        {
            ShowNotice(ex.Message, NoticeKind.Error);
            return false;
        }
        finally
        {
            _workCts.Dispose();
            _workCts = null;
            ClearProgress();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void OnCancelWork(object sender, RoutedEventArgs e) => _workCts?.Cancel();

    private void SetProgress(string text, double? fraction)
    {
        lock (_progressGate)
        {
            _progressText = text;
            if (fraction is not null) _progressFraction = fraction.Value;
        }
        RequestUpdate();
    }

    private void AddProgress(long bytes, long total)
    {
        lock (_progressGate) _progressFraction = Math.Min(1, _progressFraction + (double)bytes / total);
        RequestUpdate();
    }

    private void ClearProgress()
    {
        lock (_progressGate)
        {
            _progressText = null;
            _progressFraction = 0;
        }
        UpdateProgress();
    }

    /// <summary>Status-bar progress: local work (upload/save) if any, otherwise sync transfers.</summary>
    private void UpdateProgress()
    {
        string? text;
        double fraction;
        lock (_progressGate)
        {
            text = _progressText;
            fraction = _progressFraction;
        }
        CancelWorkButton.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        if (text is null)
        {
            var progress = _engine.GetStatus().Progress;
            if (progress.IsActive)
            {
                long total = progress.BytesDone + progress.BytesRemaining;
                text = $"Syncing {progress.FilesRemaining:N0} {(progress.FilesRemaining == 1 ? "file" : "files")} · " +
                       $"{FileRow.FormatBytes(progress.BytesDone)} of {FileRow.FormatBytes(total)}";
                fraction = progress.Fraction;
            }
        }
        ProgressPanel.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        if (text is null) return;
        ProgressText.Text = text;
        ProgressBar.Value = fraction;
        ProgressPercent.Text = $"{fraction:P0}";
    }

    // ---- Sync ------------------------------------------------------------------------------------

    private void UpdateSyncStatus()
    {
        var status = _engine.GetStatus();
        var peers = status.Peers;
        string names = peers.Count == 1 ? peers[0].DeviceName : $"{peers.Count} computers";
        bool problems = status.Problems.Count > 0;

        OffDot.Visibility = Show(status.State == SyncState.Off);
        SearchingDot.Visibility = Show(status.State == SyncState.Searching);
        SyncingIcon.Visibility = Show(status.State == SyncState.Syncing);
        SyncedIcon.Visibility = Show(status.State == SyncState.Synced && !problems);
        ProblemIcon.Visibility = Show(status.State == SyncState.Synced && problems);

        SyncStatusText.Text = status.State switch
        {
            SyncState.Searching => "Looking for your other computer…",
            SyncState.Syncing => status.Progress.IsActive ? $"Syncing with {names} · {status.Progress.Fraction:P0}" : $"Syncing with {names}…",
            SyncState.Synced when problems => $"Synced with {names} · {status.Problems.Count} {(status.Problems.Count == 1 ? "problem" : "problems")}",
            SyncState.Synced => $"Synced with {names}",
            _ => status.Mode == SyncMode.Paused ? "Sync stopped" : "Not syncing",
        };
        SyncStatusButton.ToolTip = BuildStatusTooltip(status);

        if (_shownState != status.State)
        {
            _spin.Stop(this);
            if (status.State == SyncState.Syncing) _spin.Begin(this, true);
            _shownState = status.State;
        }

        bool stoppable = status.Mode == SyncMode.Active || peers.Count > 0;
        SyncButtonText.Text = stoppable ? "Stop syncing" : "Sync";
        SyncButtonIcon.Text = stoppable ? "" : "";
        SyncMenuItem.Header = stoppable ? "_Stop syncing" : "_Sync now";

        if (status.Problems.Count > _shownProblems)
        {
            var first = status.Problems[0];
            ShowNotice(status.Problems.Count == 1
                    ? $"“{first.FileName}” couldn't sync yet: {first.Message}"
                    : $"{status.Problems.Count} files couldn't sync yet. SecureSync keeps retrying automatically.",
                NoticeKind.Warning, "Details", () => OnShowActivity(this, null!));
        }
        _shownProblems = status.Problems.Count;
    }

    private string BuildStatusTooltip(SyncStatus status)
    {
        var lines = status.Peers.Select(p => $"{p.DeviceName} ({p.Address}) — {(p.IsSynced ? "up to date" : "syncing")}").ToList();
        if (lines.Count == 0)
            lines.Add(status.Mode switch
            {
                SyncMode.Active => "Searching the local network. Unlock SecureSync on your other computer.",
                SyncMode.Paused => "Click Sync to start again.",
                _ => "Click Sync to find your other computer. It also connects if you click Sync there.",
            });
        lines.Add("");
        lines.Add($"This computer: {string.Join(", ", LocalAddresses())} (port {_engine.ListenPort})");
        lines.Add("Click for sync details.");
        return string.Join("\n", lines);
    }

    internal static IEnumerable<string> LocalAddresses()
    {
        var addresses = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                        && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString())
            .ToList();
        return addresses.Count > 0 ? addresses : ["no network"];
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void OnToggleSync(object sender, ExecutedRoutedEventArgs e)
    {
        var status = _engine.GetStatus();
        bool stoppable = status.Mode == SyncMode.Active || status.Peers.Count > 0;
        _engine.SetMode(stoppable ? SyncMode.Paused : SyncMode.Active);
        _host.SetSyncEnabled(!stoppable);
        UpdateSyncStatus();
    }

    private void OnShowActivity(object sender, ExecutedRoutedEventArgs e)
    {
        if (_activityWindow is { IsLoaded: true })
        {
            _activityWindow.Activate();
            return;
        }
        _activityWindow = new SyncActivityWindow(_engine) { Owner = Window.GetWindow(this) };
        _activityWindow.Closed += (_, _) => _activityWindow = null;
        _activityWindow.Show();
    }

    private void OnRetryNow(object sender, RoutedEventArgs e)
    {
        _engine.RetryNow();
        ShowNotice("Retrying now.", NoticeKind.Info);
    }

    private void OnConnectByIp(object sender, RoutedEventArgs e) =>
        new ConnectDialog(_engine) { Owner = Window.GetWindow(this) }.ShowDialog();

    private async void OnCheckRepair(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        (int Checked, int Damaged) result = default;
        bool done = await RunWorkAsync(ct => result = _vault.VerifyAll((i, n) =>
        {
            SetProgress($"Checking files ({i:N0} of {n:N0})", n == 0 ? 1 : (double)i / n);
        }, ct));
        if (!done) return;
        if (result.Damaged == 0)
            ShowNotice($"Checked {result.Checked:N0} files. Everything is intact.", NoticeKind.Success);
        else
            ShowNotice($"Found {result.Damaged:N0} damaged {(result.Damaged == 1 ? "file" : "files")} on this computer. Good copies download automatically from your other computer when they sync.", NoticeKind.Warning);
    }

    // ---- Other menu items ------------------------------------------------------------------------

    private async void OnShowKey(object sender, RoutedEventArgs e)
    {
        var profile = LocalProfile.Load(_host.ProfilePath);
        if (profile is null) return;
        try
        {
            using var key = await KeyProtection.UnsealAsync(profile, _host.Signer, _host.Protector); // asks for Windows Hello again
            new KeyDialog(key) { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (KeyStoreCanceledException) { }
        catch (KeyStoreException ex)
        {
            ShowNotice(ex.Message, NoticeKind.Error);
        }
    }

    private void OnShowVaultFolder(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_vault.Folder}\"") { UseShellExecute = true });

    private async void OnLock(object sender, ExecutedRoutedEventArgs e)
    {
        if (IsBusy)
        {
            if (MessageBox.Show(Window.GetWindow(this), "Files are still being processed. Stop and lock?", "Lock",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _workCts?.Cancel();
        }
        await _host.LockAsync();
    }

    private void OnExit(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();

    private void OnSecurityInfo(object sender, RoutedEventArgs e) => MessageBox.Show(Window.GetWindow(this),
        "• Your sync chain key is 256 random bits, like a Bitcoin private key. It encrypts everything, and computers prove they have it before exchanging anything.\n\n" +
        "• On this computer the key is sealed with Windows Hello and your Windows account, so it can only be unlocked with your PIN, fingerprint or face.\n\n" +
        "• Every file is encrypted with AES-256-GCM using its own key. Names, folders and dates are encrypted too, and any change to an encrypted file is detected.\n\n" +
        "• In the folder you chose, everything is stored under random names. Without the key it's unreadable — even to someone who copies the folder.\n\n" +
        "• Each connection between your computers is encrypted with fresh keys.\n\n" +
        "• Locking or closing SecureSync forgets the key and deletes temporary decrypted copies.\n\n" +
        "Keep your sync chain key secret and backed up: it's the only way to set up another computer.",
        "How your files are protected", MessageBoxButton.OK, MessageBoxImage.Information);

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        var version = typeof(ExplorerView).Assembly.GetName().Version?.ToString(3) ?? "";
        MessageBox.Show(Window.GetWindow(this), $"SecureSync {version}\n\nEncrypted file sync between your own computers.\nSync chain {_vault.Fingerprint}",
            "About SecureSync", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---- Notices ---------------------------------------------------------------------------------

    private void ShowNotice(string message, NoticeKind kind, string? actionText = null, Action? action = null)
    {
        (string glyph, string fg, string bg, string border) = kind switch
        {
            NoticeKind.Error => ("", "DangerBrush", "#FDE7E9", "#F1BBBC"),
            NoticeKind.Warning => ("", "WarningBrush", "#FFF4CE", "#F2D37C"),
            NoticeKind.Success => ("", "SuccessBrush", "#DFF6DD", "#A6D8A3"),
            _ => ("", "AccentBrush", "#F0F6FC", "#C5DCF2"),
        };
        NoticeIcon.Text = glyph;
        NoticeIcon.Foreground = (Brush)FindResource(fg);
        NoticeBar.Background = (Brush)new BrushConverter().ConvertFromString(bg)!;
        NoticeBar.BorderBrush = (Brush)new BrushConverter().ConvertFromString(border)!;
        NoticeText.Text = message;
        _noticeAction = action;
        NoticeAction.Content = actionText;
        NoticeAction.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        NoticeBar.Visibility = Visibility.Visible;
        _noticeTimer.Stop();
        _noticeTimer.Interval = TimeSpan.FromSeconds(kind is NoticeKind.Error or NoticeKind.Warning ? 20 : 8);
        _noticeTimer.Start();
    }

    private void HideNotice()
    {
        _noticeTimer.Stop();
        NoticeBar.Visibility = Visibility.Collapsed;
    }

    private void OnDismissNotice(object sender, RoutedEventArgs e) => HideNotice();

    private void OnNoticeAction(object sender, RoutedEventArgs e)
    {
        HideNotice();
        _noticeAction?.Invoke();
    }
}
