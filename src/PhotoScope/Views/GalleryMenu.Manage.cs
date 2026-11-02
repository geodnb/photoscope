using System.Windows.Controls;
using PhotoScope.Models;
using PhotoScope.ViewModels;

namespace PhotoScope.Views;

public static partial class GalleryMenu
{
    static partial void AddManagementItems(ContextMenu menu, MainViewModel vm, List<Photo> photos, Photo? single,
        Func<string, string, Action, string?, bool, MenuItem> item)
    {
        var anyMissing = photos.Any(p => p.IsMissing);
        var singlePresent = single is { IsMissing: false };

        menu.Items.Add(item(photos.Count == 1 ? "Edit tags…" : "Add tags…", "\uE8EC", () => vm.EditTagsSelectedCommand.Execute(null), null, true));
        menu.Items.Add(new Separator());
        menu.Items.Add(item("Rename…", "\uE8AC", () => vm.RenameSelectedCommand.Execute(null), "F2", singlePresent));
        menu.Items.Add(item("Move to folder…", "\uE8DE", () => vm.MoveSelectedCommand.Execute(null), null, !photos.All(p => p.IsMissing)));
        menu.Items.Add(item("Copy to folder…", "\uE8C8", () => vm.CopySelectedCommand.Execute(null), null, !photos.All(p => p.IsMissing)));
        menu.Items.Add(new Separator());
        menu.Items.Add(item("Open containing folder", "\uE838", () => vm.OpenContainingFolderCommand.Execute(null), null, single != null));
        menu.Items.Add(item("Show in Explorer", "\uEC50", () => vm.ShowInExplorerCommand.Execute(null), null, single != null));
        menu.Items.Add(new Separator());
        if (anyMissing)
            menu.Items.Add(item("Relocate…", "\uE721", () => vm.RelocateSelectedCommand.Execute(null), null, true));
        menu.Items.Add(item("Remove from library…", "\uE738", () => vm.RemoveSelectedFromLibraryCommand.Execute(null), null, true));
        menu.Items.Add(item("Delete…", "\uE74D", () => vm.DeleteSelectedCommand.Execute(null), "Del", true));
    }
}
