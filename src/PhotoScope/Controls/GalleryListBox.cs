using System.Collections;
using System.Windows.Controls;

namespace PhotoScope.Controls;

/// <summary>ListBox used for the photo grid. Adds bulk selection (and, later, drag support).</summary>
public class GalleryListBox : ListBox
{
    /// <summary>Selects exactly these items in one operation.</summary>
    public void SelectItems(IEnumerable items) => SetSelectedItems(items);
}
