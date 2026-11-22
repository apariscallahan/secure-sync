namespace SecureSync.Mobile;

/// <summary>Shows the sync chain key (only after fingerprint/screen-lock verification) to add another device.</summary>
public sealed class KeyPage : ContentPage
{
    public KeyPage(string key, string fingerprint)
    {
        Title = "Sync chain key";
        var keyLabel = new Label
        {
            Text = key,
            FontFamily = "monospace",
            FontSize = 17,
            TextColor = (Color)Application.Current!.Resources["BrandDark"],
            LineBreakMode = LineBreakMode.CharacterWrap,
        };
        var copy = new Button { Text = "Copy", Style = (Style)Application.Current.Resources["Outlined"] };
        copy.Clicked += async (_, _) =>
        {
            await Clipboard.Default.SetTextAsync(key);
            await DisplayAlertAsync("Copied", "The key is on the clipboard. Paste it where you need it, then copy something else so it doesn't stay there.", "OK");
        };
        var share = new Button { Text = "Share…", Style = (Style)Application.Current.Resources["Outlined"] };
        share.Clicked += async (_, _) => await Share.Default.RequestAsync(new ShareTextRequest { Title = "SecureSync sync chain key", Text = key });

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(24),
                Spacing = 14,
                Children =
                {
                    new Label { Text = $"Key for sync chain {fingerprint}", Style = (Style)Application.Current.Resources["Muted"] },
                    new Border { Style = (Style)Application.Current.Resources["Card"], BackgroundColor = (Color)Application.Current.Resources["BrandSoft"], Stroke = Colors.Transparent, Content = keyLabel },
                    new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 12, Children = { copy, share } },
                    new Label
                    {
                        Style = (Style)Application.Current.Resources["Muted"],
                        Text = "Enter this key on another device (Join an existing sync chain) to add it. Keep it secret: anyone who has it can read your files.",
                    },
                },
            },
        };
        Grid.SetColumn(share, 1);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        Content = null; // don't keep the key on screen in the back stack
    }
}
