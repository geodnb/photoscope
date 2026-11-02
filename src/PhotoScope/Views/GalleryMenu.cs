using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using PhotoScope.Controls;
using PhotoScope.ViewModels;

namespace PhotoScope.Views;

/// <summary>Builds the gallery's right-click menu for the current selection.</summary>
public static partial class GalleryMenu
{
    public static ContextMenu Build(MainViewModel vm)
    {
        var photos = vm.SelectedPhotos.ToList();
        var single = photos.Count == 1 ? photos[0] : null;
        var menu = new ContextMenu();

        MenuItem Item(string header, string icon, Action run, string? gesture = null, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled, Command = new RelayCommand(run) };
            Ui.SetIcon(mi, icon);
            return mi;
        }

        menu.Items.Add(Item("Open", "\uE740", () => vm.OpenViewerCommand.Execute(single ?? vm.SelectedPhoto), "Enter"));
        menu.Items.Add(Item(photos.Count > 1 ? $"Slideshow of {photos.Count:N0} photos" : "Slideshow from here", "\uE768",
            () => vm.SlideshowCommand.Execute(null)));
        menu.Items.Add(new Separator());

        // Add to playlist ▸
        var add = new MenuItem { Header = "Add to playlist" };
        Ui.SetIcon(add, "\uE8FD");
        foreach (var pl in vm.Playlists)
            add.Items.Add(Item(pl.Name, "", () => vm.AddSelectedToPlaylistCommand.Execute(pl)));
        if (vm.Playlists.Count > 0) add.Items.Add(new Separator());
        add.Items.Add(Item("New playlist…", "\uE710", () => vm.AddSelectedToPlaylistCommand.Execute(null)));
        menu.Items.Add(add);
        menu.Items.Add(new Separator());

        var allFav = photos.All(p => p.IsFavourite);
        menu.Items.Add(Item(allFav ? "Remove from favourites" : "Add to favourites", allFav ? "\uEB52" : "\uEB51",
            () => vm.ToggleFavourite(photos), "F"));

        var rating = new MenuItem { Header = "Rating" };
        Ui.SetIcon(rating, "\uE734");
        for (var r = 0; r <= 5; r++)
        {
            var value = r;
            var mi = Item(r == 0 ? "No rating" : new string('★', r) + new string('☆', 5 - r), "", () => vm.SetRating(photos, value), r.ToString());
            mi.IsChecked = photos.All(p => p.Rating == value);
            rating.Items.Add(mi);
        }
        menu.Items.Add(rating);

        menu.Items.Add(Item("Rotate left", "\uE7AD", () => vm.Rotate(photos, -90)));
        menu.Items.Add(Item("Rotate right", "\uE7AD", () => vm.Rotate(photos, 90)));

        AddManagementItems(menu, vm, photos, single, Item);
        return menu;
    }

    static partial void AddManagementItems(ContextMenu menu, MainViewModel vm, List<Models.Photo> photos, Models.Photo? single,
        Func<string, string, Action, string?, bool, MenuItem> item);
}
