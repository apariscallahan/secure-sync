using System.Windows;
using System.Windows.Controls;

namespace SecureSync.App.Views;

/// <summary>A small task dialog: a question and a few command-link choices, plus Cancel.</summary>
public sealed class ChoiceDialog : Window
{
    private int _choice = -1;

    private ChoiceDialog(string title, string message, IReadOnlyList<string> choices)
    {
        Style = (Style)Application.Current.FindResource("DialogWindow");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;

        var list = new StackPanel { Margin = new Thickness(-8, 12, 0, 0) };
        for (int i = 0; i < choices.Count; i++)
        {
            int index = i;
            var button = new Button
            {
                Style = (Style)FindResource("CommandLink"),
                Content = new TextBlock { Text = choices[i], FontSize = 13, Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush"), TextWrapping = TextWrapping.Wrap },
                Margin = new Thickness(0, 0, 0, 2),
            };
            button.Click += (_, _) =>
            {
                _choice = index;
                DialogResult = true;
            };
            list.Children.Add(button);
        }

        var cancel = new Button { Content = "Cancel", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        var footer = new Border { Style = (Style)FindResource("DialogFooter"), Child = cancel };
        DockPanel.SetDock(footer, Dock.Bottom);
        var body = new StackPanel { Margin = new Thickness(20, 18, 20, 14) };
        body.Children.Add(new TextBlock { Text = message, Style = (Style)FindResource("DialogHeading") });
        body.Children.Add(list);
        var root = new DockPanel();
        root.Children.Add(footer);
        root.Children.Add(body);
        Content = root;
    }

    /// <summary>Returns the index of the chosen option, or -1 if canceled.</summary>
    public static int Show(Window? owner, string title, string message, IReadOnlyList<string> choices)
    {
        var dialog = new ChoiceDialog(title, message, choices) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog._choice : -1;
    }
}
