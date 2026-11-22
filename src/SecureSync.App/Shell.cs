using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SecureSync.App;

/// <summary>Windows shell look-ups: the same file icons, type names and name ordering File Explorer uses.</summary>
internal static class Shell
{
    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiSmallIcon = 0x1;
    private const uint ShgfiUseFileAttributes = 0x10;
    private const uint ShgfiTypeName = 0x400;
    private const uint FileAttributeNormal = 0x80;
    private const uint FileAttributeDirectory = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref ShFileInfo psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);

    private static readonly Dictionary<string, (ImageSource? Icon, string Type)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Explorer's "natural" order: "file 2" before "file 10", case-insensitive.</summary>
    public static readonly IComparer<string> NaturalOrder = Comparer<string>.Create(StrCmpLogicalW);

    public static (ImageSource? Icon, string TypeName) ForFile(string name)
    {
        var ext = Path.GetExtension(name);
        var info = Lookup("file" + ext, FileAttributeNormal);
        return (info.Icon, string.IsNullOrEmpty(info.Type) ? $"{ext.TrimStart('.').ToUpperInvariant()} File".Trim() : info.Type);
    }

    public static (ImageSource? Icon, string TypeName) ForFolder()
    {
        var info = Lookup("folder", FileAttributeDirectory);
        return (info.Icon, string.IsNullOrEmpty(info.Type) ? "File folder" : info.Type);
    }

    private static (ImageSource? Icon, string Type) Lookup(string fakePath, uint attributes)
    {
        var key = fakePath + "|" + attributes;
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var info = new ShFileInfo();
        ImageSource? icon = null;
        string type = "";
        if (SHGetFileInfo(fakePath, attributes, ref info, (uint)Marshal.SizeOf<ShFileInfo>(),
                ShgfiIcon | ShgfiSmallIcon | ShgfiUseFileAttributes | ShgfiTypeName) != IntPtr.Zero)
        {
            type = info.szTypeName ?? "";
            if (info.hIcon != IntPtr.Zero)
            {
                try
                {
                    var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    icon = source;
                }
                finally
                {
                    DestroyIcon(info.hIcon);
                }
            }
        }
        return Cache[key] = (icon, type);
    }
}
