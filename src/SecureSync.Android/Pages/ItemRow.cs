using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using SecureSync.Core.Storage;

namespace SecureSync.Mobile;

/// <summary>One file or folder in the phone's file list.</summary>
public sealed class ItemRow : INotifyPropertyChanged
{
    private VaultItem _item;
    private string _detail = "";
    private string? _statusIcon;

    public ItemRow(VaultItem item, string? location)
    {
        _item = item;
        Location = location;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public VaultItem Item
    {
        get => _item;
        set
        {
            _item = value;
            Raise(nameof(Name));
            Raise(nameof(Icon));
        }
    }

    public ObjectId Id => _item.Id;
    public bool IsFolder => _item.IsFolder;
    public string Name => _item.Name;
    public string? Location { get; }
    public string Icon => IconFor(_item);

    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public string? StatusIcon { get => _statusIcon; private set => Set(ref _statusIcon, value); }

    public void SetStatus(string detail, string? statusIcon)
    {
        Detail = detail;
        StatusIcon = statusIcon;
    }

    private static string IconFor(VaultItem item)
    {
        if (item.IsFolder) return "type_folder.png";
        return Path.GetExtension(item.Name).TrimStart('.').ToLowerInvariant() switch
        {
            "jpg" or "jpeg" or "png" or "gif" or "bmp" or "webp" or "heic" or "heif" or "tif" or "tiff" or "svg" => "type_image.png",
            "mp4" or "mov" or "avi" or "mkv" or "wmv" or "webm" or "m4v" or "3gp" => "type_video.png",
            "mp3" or "wav" or "flac" or "m4a" or "aac" or "ogg" or "opus" => "type_audio.png",
            "pdf" => "type_pdf.png",
            "doc" or "docx" or "odt" or "rtf" or "txt" or "md" => "type_doc.png",
            "xls" or "xlsx" or "csv" or "ods" => "type_sheet.png",
            _ => "type_file.png",
        };
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    public static string FormatDate(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var culture = CultureInfo.CurrentCulture;
        if (local.Date == DateTime.Today) return local.ToString("t", culture);
        return local.Year == DateTime.Today.Year ? local.ToString("MMM d", culture) : local.ToString("MMM d, yyyy", culture);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name!);
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
