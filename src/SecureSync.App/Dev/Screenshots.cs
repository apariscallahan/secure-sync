#if DEBUG
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SecureSync.App.Views;
using SecureSync.Core.Crypto;
using SecureSync.Core.Security;
using SecureSync.Core.Storage;
using SecureSync.Core.Sync;

namespace SecureSync.App.Dev;

/// <summary>
/// Development aid (Debug builds only): drives the real windows through setup, browsing, renaming and a live sync with
/// a second in-process device, saving PNGs for visual review. Windows Hello is replaced by a stand-in so nothing prompts.
/// </summary>
internal static class Screenshots
{
    private sealed class DevSigner : IKeySigner
    {
        private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);
        public Task<SignerAvailability> CheckAvailabilityAsync() => Task.FromResult(SignerAvailability.Available);
        public Task<byte[]> SignAsync(byte[] data, CancellationToken ct) => Task.FromResult(HMACSHA512.HashData(_secret, data));
    }

    public static async void Render(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        try
        {
            await RenderAllAsync(outputDir);
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(outputDir, "error.txt"), e.ToString());
        }
        finally
        {
            Application.Current.Shutdown();
        }
    }

    private static async Task RenderAllAsync(string outputDir)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "securesync-screens-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(tmp);
        var window = new MainWindow(new DevSigner(), Path.Combine(tmp, "profile.json"))
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = 0,
            ShowActivated = false,
            Width = 1100,
            Height = 700,
        };
        window.Show();

        // Setup: welcome → create → key
        var lockView = (LockView)window.Host.Content;
        await Save(window, outputDir, "01-setup-welcome.png");
        Click((Button)lockView.WelcomePage.Children[0]);
        lockView.FolderBox.Text = Path.Combine(tmp, "Office PC vault");
        lockView.DeviceBox.Text = "Office PC";
        await Save(window, outputDir, "02-setup-create.png");
        Click(lockView.PrimaryButton);
        await WaitFor(() => lockView.KeyPage.Visibility == Visibility.Visible);
        await Save(window, outputDir, "03-setup-key.png");
        var keyText = lockView.KeyText.Text;
        lockView.SavedKeyCheck.IsChecked = true;
        Click(lockView.PrimaryButton);

        // Main window, empty
        var explorer = (ExplorerView)window.Host.Content;
        await Save(window, outputDir, "04-explorer-empty.png");

        // Sample content: some on this computer, some on a second device that syncs in.
        var vaultA = window.CurrentVault!;
        var src = Path.Combine(tmp, "src");
        string Write(string name, int kb)
        {
            var path = Path.Combine(src, Guid.NewGuid().ToString("N"), name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(kb * 1024));
            return path;
        }
        var contracts = vaultA.CreateFolder(null, "Contracts");
        vaultA.AddFile(Write("Lease agreement.docx", 120), contracts.Id);
        vaultA.AddFile(Write("Car insurance.pdf", 340), contracts.Id);
        vaultA.AddFile(Write("Tax return 2025.pdf", 812), null);
        vaultA.AddFile(Write("Budget 2026.xlsx", 64), null);
        vaultA.AddFile(Write("Passport scan.jpg", 2_450), null);
        vaultA.CreateFolder(null, "Taxes");

        Assert(ChainKey.TryParse(keyText, out var key));
        using var vaultB = Vault.Open(Path.Combine(tmp, "Laptop vault"), key!, "Laptop");
        var photos = vaultB.CreateFolder(null, "Photos");
        var iceland = vaultB.CreateFolder(photos.Id, "Iceland trip");
        vaultB.AddFile(Write("Glacier lagoon.jpg", 3_100), iceland.Id);
        vaultB.AddFile(Write("Northern lights.jpg", 2_870), iceland.Id);
        vaultB.AddFile(Write("Black sand beach.heic", 1_960), iceland.Id);
        vaultB.AddFile(Write("Waterfall.mp4", 9_400), iceland.Id);
        vaultB.AddFile(Write("Birthday.jpg", 1_400), photos.Id);
        vaultB.AddFile(Write("Recovery codes.txt", 2), null);
        await using var engineB = new SyncEngine(vaultB);
        engineB.Start();
        engineB.SetMode(SyncMode.Active);
        await WaitFor(() => window.Engine!.GetStatus() is { State: SyncState.Synced, Progress.IsActive: false } && vaultA.Snapshot().Items.Count == 15, 30);
        await Save(window, outputDir, "05-explorer-synced.png");

        // A big file arriving: progress bar.
        var big = Path.Combine(src, "Family video.mp4");
        await using (var fs = File.Create(big)) fs.SetLength(600L * 1024 * 1024);
        vaultB.AddFile(big, null);
        await WaitFor(() => window.Engine!.GetStatus().Progress is { IsActive: true, Fraction: > 0.15 }, 30, pollMs: 20);
        await Save(window, outputDir, "06-explorer-syncing.png", settleMs: 0);
        await WaitFor(() => window.Engine!.GetStatus() is { State: SyncState.Synced, Progress.IsActive: false }, 60);

        // Navigate into a folder, select, rename inline.
        Click((Button)explorer.Breadcrumb.Children[1]);
        var tree = explorer.FolderTree;
        explorer.FileList.Focus();
        Invoke(explorer, "NavigateTo", (ObjectId?)iceland.Id, true);
        await Save(window, outputDir, "07-explorer-folder.png");
        explorer.FileList.SelectedIndex = 1;
        Commands.Rename.Execute(null, explorer.FileList);
        await Save(window, outputDir, "08-explorer-rename.png");
        if (explorer.FileList.SelectedItem is FileRow editing) editing.IsEditing = false;

        // Menus and context menu (popups rendered on their own).
        Invoke(explorer, "NavigateTo", (ObjectId?)null, true);
        await Save(window, outputDir, "09-explorer-top.png");
        var fileMenu = (MenuItem)((Menu)((DockPanel)explorer.Content).Children[0]).Items[0];
        fileMenu.IsSubmenuOpen = true;
        await Task.Delay(300);
        if (fileMenu.Template.FindName("PART_Popup", fileMenu) is Popup { Child: FrameworkElement menuPanel })
            await SaveElement(menuPanel, outputDir, "10-menu-file.png");
        fileMenu.IsSubmenuOpen = false;
        explorer.FileList.SelectedIndex = 3;
        explorer.ItemMenu.PlacementTarget = explorer.FileList;
        explorer.ItemMenu.IsOpen = true;
        await Task.Delay(300);
        await SaveElement(explorer.ItemMenu, outputDir, "11-context-menu.png");
        explorer.ItemMenu.IsOpen = false;

        // Dialogs.
        var activity = new SyncActivityWindow(window.Engine!) { Owner = window, ShowActivated = false };
        activity.Show();
        await Save(activity, outputDir, "12-sync-activity.png");
        activity.Close();
        var move = new MoveToDialog(vaultA.Snapshot(), new HashSet<ObjectId>(), iceland.Id) { Owner = window, ShowActivated = false };
        move.Show();
        await Save(move, outputDir, "13-move-to.png");
        move.Close();

        // Lock → unlock screen.
        await window.LockAsync();
        await Save(window, outputDir, "14-locked.png");
        key!.Dispose();
        window.Close();
        try { Directory.Delete(tmp, recursive: true); } catch (IOException) { }
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Invoke(object target, string method, params object?[] args) =>
        target.GetType().GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(target, args);

    private static void Assert(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Screenshot setup failed.");
    }

    private static async Task WaitFor(Func<bool> condition, int seconds = 10, int pollMs = 100)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Screenshot step timed out.");
            await Task.Delay(pollMs);
        }
    }

    private static async Task Save(Window window, string dir, string name, int settleMs = 600)
    {
        if (settleMs > 0) await Task.Delay(settleMs);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        int width = (int)Math.Ceiling(root.ActualWidth + root.Margin.Left + root.Margin.Right);
        int height = (int)Math.Ceiling(root.ActualHeight + root.Margin.Top + root.Margin.Bottom);
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(background);
        bitmap.Render(root);
        Write(bitmap, Path.Combine(dir, name));
    }

    private static async Task SaveElement(FrameworkElement element, string dir, string name)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        int width = (int)Math.Ceiling(element.ActualWidth), height = (int)Math.Ceiling(element.ActualHeight);
        if (width == 0 || height == 0) return;
        var bitmap = new RenderTargetBitmap(width + 20, height + 20, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)), null, new Rect(0, 0, width + 20, height + 20));
        bitmap.Render(background);
        bitmap.Render(element);
        Write(bitmap, Path.Combine(dir, name));
    }

    private static void Write(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
#endif
