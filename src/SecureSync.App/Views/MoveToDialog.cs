using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SecureSync.Core.Storage;

namespace SecureSync.App.Views;

/// <summary>"Move to…": pick a destination folder from the tree (folders being moved, and their insides, are left out).</summary>
public sealed class MoveToDialog : Window
{
    private readonly TreeView _tree;
    private readonly Button _ok;

    public MoveToDialog(VaultSnapshot snapshot, ISet<ObjectId> moving, ObjectId? current)
    {
        Style = (Style)Application.Current.FindResource("DialogWindow");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Title = "Move items";
        Width = 380;
        Height = 460;
        ResizeMode = ResizeMode.CanResizeWithGrip;

        var folderIcon = Shell.ForFolder().Icon;
        var root = new FolderNode(null, "SecureSync", (ImageSource)FindResource("LogoImage")) { IsExpanded = true };
        void Fill(FolderNode node, ObjectId? id)
        {
            foreach (var folder in snapshot.Children(id).Where(i => i.IsFolder && !moving.Contains(i.Id)).OrderBy(i => i.Name, Shell.NaturalOrder))
            {
                var child = new FolderNode(folder.Id, folder.Name, folderIcon);
                node.Children.Add(child);
                Fill(child, folder.Id);
            }
        }
        Fill(root, null);

        // Start at the current folder.
        var selected = root;
        foreach (var step in snapshot.PathTo(current))
        {
            selected.IsExpanded = true;
            selected = selected.Children.FirstOrDefault(c => c.Id == step.Id) ?? selected;
        }
        selected.IsSelected = true;

        _tree = new TreeView
        {
            Style = (Style)FindResource("FolderTree"),
            ItemsSource = new[] { root },
            BorderBrush = (Brush)FindResource("ControlBorderBrush"),
            BorderThickness = new Thickness(1),
            ItemTemplate = (DataTemplate)FindResource("FolderNodeTemplate"),
        };
        _tree.SelectedItemChanged += (_, _) => _ok!.IsEnabled = _tree.SelectedItem is not null;
        _tree.MouseDoubleClick += (_, e) =>
        {
            if (_tree.SelectedItem is not null && e.OriginalSource is FrameworkElement { DataContext: FolderNode }) DialogResult = true;
        };

        _ok = new Button { Content = "Move", Style = (Style)FindResource("AccentButton"), IsDefault = true };
        _ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_ok);
        buttons.Children.Add(cancel);
        var footer = new Border { Style = (Style)FindResource("DialogFooter"), Child = buttons };
        DockPanel.SetDock(footer, Dock.Bottom);

        var heading = new TextBlock { Text = "Choose where to move the selected items:", Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(heading, Dock.Top);
        var body = new DockPanel { Margin = new Thickness(16, 14, 16, 14) };
        body.Children.Add(heading);
        body.Children.Add(_tree);

        var layout = new DockPanel();
        layout.Children.Add(footer);
        layout.Children.Add(body);
        Content = layout;
        Loaded += (_, _) => _tree.Focus();
    }

    public ObjectId? Destination => (_tree.SelectedItem as FolderNode)?.Id;
}
