using PhotoScope.Models;

namespace PhotoScope.ViewModels;

// Temporary stubs for stage 1; replaced by the playlist and management partials in later stages.
public sealed partial class MainViewModel
{
    void InitPlaylists() { }
    void LoadPlaylists() { }
    void RemoveFromLoadedPlaylist(HashSet<Photo> removed) { }
    void RelocateMissing(IReadOnlyList<Photo> photos) { }
    void RemoveFromLibrary(IReadOnlyList<Photo> photos) { }
}
