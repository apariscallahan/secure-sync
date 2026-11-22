using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using SecureSync.Core.Storage;

namespace SecureSync.App.Views;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One row in the details list.</summary>
public sealed class FileRow : Observable
{
    private VaultItem _item;
    private string _statusGlyph = "";
    private Brush? _statusBrush;
    private string _statusText = "";
    private bool _isEditing;
    private string _editName = "";
    private bool _isCut;
    private bool _isDropTarget;

    public FileRow(VaultItem item, string? location)
    {
        _item = item;
        Location = location;
        var (icon, type) = item.IsFolder ? Shell.ForFolder() : Shell.ForFile(item.Name);
        Icon = icon;
        TypeName = type;
    }

    public VaultItem Item
    {
        get => _item;
        set
        {
            if (ReferenceEquals(_item, value)) return;
            bool renamed = _item.Name != value.Name;
            _item = value;
            if (renamed)
            {
                var (icon, type) = value.IsFolder ? Shell.ForFolder() : Shell.ForFile(value.Name);
                Icon = icon;
                TypeName = type;
                Raise(nameof(Icon));
                Raise(nameof(TypeName));
            }
            Raise(nameof(Name));
            Raise(nameof(SizeText));
            Raise(nameof(ModifiedText));
        }
    }

    public ObjectId Id => _item.Id;
    public bool IsFolder => _item.IsFolder;
    public string Name => _item.Name;
    public ImageSource? Icon { get; private set; }
    public string TypeName { get; private set; }
    public string? Location { get; }
    public string LocationSuffix => Location is null ? "" : $"   {Location}";
    public string SizeText => IsFolder ? "" : FormatSize(_item.Size);
    public DateTime Modified => IsFolder ? _item.ChangedUtc : _item.ModifiedUtc;
    public string ModifiedText => FormatDate(Modified);

    public string StatusGlyph { get => _statusGlyph; private set => Set(ref _statusGlyph, value); }
    public Brush? StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public void SetStatus(string glyph, Brush? brush, string text)
    {
        StatusGlyph = glyph;
        StatusBrush = brush;
        StatusText = text;
    }

    public bool IsEditing
    {
        get => _isEditing;
        set { if (Set(ref _isEditing, value)) Raise(nameof(EditVisibility)); }
    }

    public Visibility EditVisibility => _isEditing ? Visibility.Visible : Visibility.Collapsed;
    public string EditName { get => _editName; set => Set(ref _editName, value); }

    public bool IsCut
    {
        get => _isCut;
        set { if (Set(ref _isCut, value)) Raise(nameof(IconOpacity)); }
    }

    public double IconOpacity => _isCut ? 0.45 : 1;
    public bool IsDropTarget { get => _isDropTarget; set => Set(ref _isDropTarget, value); }

    public static string FormatSize(long bytes)
    {
        // Explorer shows sizes in whole KB, rounded up.
        if (bytes == 0) return "0 KB";
        if (bytes < 1024L * 1024 * 1024) return $"{(bytes + 1023) / 1024:N0} KB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:N0} bytes" : $"{value:0.#} {units[unit]}";
    }

    public static string FormatDate(DateTime utc)
    {
        if (utc == default) return "";
        var local = utc.ToLocalTime();
        var culture = CultureInfo.CurrentCulture;
        return local.ToString("d", culture) + " " + local.ToString("t", culture);
    }
}

/// <summary>A folder in the navigation tree.</summary>
public sealed class FolderNode : Observable
{
    private string _name;
    private bool _isExpanded;
    private bool _isSelected;
    private bool _isDropTarget;

    public FolderNode(ObjectId? id, string name, ImageSource? icon)
    {
        Id = id;
        _name = name;
        Icon = icon;
    }

    public ObjectId? Id { get; }
    public string Name { get => _name; set => Set(ref _name, value); }
    public ImageSource? Icon { get; }
    public System.Collections.ObjectModel.ObservableCollection<FolderNode> Children { get; } = [];
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public bool IsDropTarget { get => _isDropTarget; set => Set(ref _isDropTarget, value); }
}
