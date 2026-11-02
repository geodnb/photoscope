using System.Windows;
using System.Windows.Media;
using PhotoScope.Services;

namespace PhotoScope.Controls;

/// <summary>Base window: theme colours, Segoe UI Variable, and a title bar that follows the theme.</summary>
public class ThemedWindow : Window
{
    public ThemedWindow()
    {
        SetResourceReference(BackgroundProperty, "Brush.Window");
        SetResourceReference(ForegroundProperty, "Brush.Text");
        SetResourceReference(FontFamilyProperty, "Font.UI");
        FontSize = 14;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
        App.SendToBack(this);
    }
}
