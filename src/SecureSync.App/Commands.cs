using System.Windows.Input;

namespace SecureSync.App;

/// <summary>App commands, so menus, toolbar buttons and keyboard shortcuts share one definition.</summary>
public static class Commands
{
    private static RoutedUICommand Make(string text, string name, params InputGesture[] gestures) =>
        new(text, name, typeof(Commands), [.. gestures]);

    public static readonly RoutedUICommand UploadFiles = Make("Upload _files…", nameof(UploadFiles), new KeyGesture(Key.U, ModifierKeys.Control));
    public static readonly RoutedUICommand UploadFolder = Make("Upload f_older…", nameof(UploadFolder), new KeyGesture(Key.U, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand NewFolder = Make("New _folder", nameof(NewFolder), new KeyGesture(Key.N, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand OpenItem = Make("_Open", nameof(OpenItem));
    public static readonly RoutedUICommand SaveCopy = Make("_Save decrypted copy…", nameof(SaveCopy), new KeyGesture(Key.S, ModifierKeys.Control));
    public static readonly RoutedUICommand Rename = Make("Re_name", nameof(Rename), new KeyGesture(Key.F2));
    public static readonly RoutedUICommand MoveTo = Make("_Move to…", nameof(MoveTo), new KeyGesture(Key.M, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand Refresh = Make("_Refresh", nameof(Refresh), new KeyGesture(Key.F5));
    public static readonly RoutedUICommand Back = Make("_Back", nameof(Back), new KeyGesture(Key.Left, ModifierKeys.Alt));
    public static readonly RoutedUICommand Forward = Make("_Forward", nameof(Forward), new KeyGesture(Key.Right, ModifierKeys.Alt));
    public static readonly RoutedUICommand Up = Make("_Up one level", nameof(Up), new KeyGesture(Key.Up, ModifierKeys.Alt));
    public static readonly RoutedUICommand Search = Make("_Search", nameof(Search), new KeyGesture(Key.F, ModifierKeys.Control), new KeyGesture(Key.E, ModifierKeys.Control));
    public static readonly RoutedUICommand Lock = Make("_Lock", nameof(Lock), new KeyGesture(Key.L, ModifierKeys.Control));
    public static readonly RoutedUICommand ToggleSync = Make("_Sync now", nameof(ToggleSync));
    public static readonly RoutedUICommand SyncActivity = Make("Sync _activity…", nameof(SyncActivity));
}
