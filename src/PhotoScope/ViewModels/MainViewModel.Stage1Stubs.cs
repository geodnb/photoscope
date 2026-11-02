using PhotoScope.Models;

namespace PhotoScope.ViewModels;

// Temporary stubs; replaced by the management partial in stage 4.
public sealed partial class MainViewModel
{
    void RelocateMissing(IReadOnlyList<Photo> photos) { }
    void RemoveFromLibrary(IReadOnlyList<Photo> photos) { }
    public IReadOnlyCollection<Photo> DeleteInteractive(IReadOnlyList<Photo> photos) => [];
}
