using SecureSync.Core.Storage;

namespace SecureSync.Mobile;

/// <summary>"Move to": every folder as an indented list; tap one to move there.</summary>
public sealed class FolderPickerPage : ContentPage
{
    public readonly record struct Choice(ObjectId? Folder);

    private readonly TaskCompletionSource<Choice?> _result = new();

    private sealed record FolderOption(ObjectId? Id, string Name, int Depth, bool IsCurrent)
    {
        public Thickness Indent => new(16 + Depth * 24, 12, 16, 12);
        public string Label => IsCurrent ? $"{Name}  (current)" : Name;
    }

    private FolderPickerPage(VaultSnapshot snapshot, ObjectId moving, ObjectId? current)
    {
        Title = "Move to";
        var options = new List<FolderOption> { new(null, "SecureSync (top level)", 0, current is null) };
        void Add(ObjectId? parent, int depth)
        {
            foreach (var folder in snapshot.Children(parent).Where(i => i.IsFolder && i.Id != moving)
                         .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                options.Add(new FolderOption(folder.Id, folder.Name, depth, folder.Id == current));
                Add(folder.Id, depth + 1);
            }
        }
        Add(null, 1);

        var list = new CollectionView
        {
            ItemsSource = options,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new DataTemplate(() =>
            {
                var icon = new Image { Source = "type_folder.png", WidthRequest = 28, HeightRequest = 28 };
                var label = new Label { VerticalOptions = LayoutOptions.Center, FontSize = 16 };
                label.SetBinding(Label.TextProperty, nameof(FolderOption.Label));
                var row = new HorizontalStackLayout { Spacing = 14, Children = { icon, label } };
                row.SetBinding(Microsoft.Maui.Controls.Layout.PaddingProperty, nameof(FolderOption.Indent));
                return row;
            }),
        };
        list.SelectionChanged += async (_, e) =>
        {
            if (e.CurrentSelection.FirstOrDefault() is not FolderOption option) return;
            _result.TrySetResult(new Choice(option.Id));
            await Navigation.PopAsync();
        };
        Content = list;
    }

    /// <summary>Returns the chosen folder (Folder = null means the top level), or null if the user backed out.</summary>
    public static async Task<Choice?> PickAsync(INavigation navigation, VaultSnapshot snapshot, ObjectId moving, ObjectId? current)
    {
        var page = new FolderPickerPage(snapshot, moving, current);
        await navigation.PushAsync(page);
        return await page._result.Task;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _result.TrySetResult(null);
    }
}
