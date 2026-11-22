using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SecureSync.Core.Crypto;

namespace SecureSync.App.Views;

/// <summary>Shows the sync chain key (after Windows Hello) so it can be entered on another computer or backed up.</summary>
public sealed class KeyDialog : Window
{
    public KeyDialog(ChainKey key)
    {
        Style = (Style)Application.Current.FindResource("DialogWindow");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Title = "Sync chain key";
        Width = 500;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;

        var text = key.Reveal();
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            FontFamily = (FontFamily)FindResource("MonoFont"),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 10, 0, 8),
        };
        var copy = new Button { Content = "Copy" };
        copy.Click += (_, _) => Clipboard.SetText(text);
        var save = new Button { Content = "Save to file…", Margin = new Thickness(8, 0, 0, 0) };
        save.Click += (_, _) => LockView.SaveKeyFile(this, key);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(copy);
        actions.Children.Add(save);

        var body = new StackPanel { Margin = new Thickness(20, 18, 20, 16) };
        body.Children.Add(new TextBlock { Text = $"Key for sync chain {key.Fingerprint}", Style = (Style)FindResource("DialogHeading") });
        body.Children.Add(box);
        body.Children.Add(actions);
        body.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Hint"),
            Margin = new Thickness(0, 12, 0, 0),
            Text = "Enter this key on another computer to add it to the chain. Keep it secret: anyone who has it can read your files.",
        });

        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        var footer = new Border { Style = (Style)FindResource("DialogFooter"), Child = close };
        DockPanel.SetDock(footer, Dock.Bottom);
        var layout = new DockPanel();
        layout.Children.Add(footer);
        layout.Children.Add(body);
        Content = layout;
        Closed += (_, _) => box.Clear();
    }
}
