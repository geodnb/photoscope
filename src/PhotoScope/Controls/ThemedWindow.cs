using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoScope.Services;

namespace PhotoScope.Controls;

/// <summary>Base window: theme colours, Segoe UI Variable, and a title bar that follows the theme.</summary>
public class ThemedWindow : Window
{
    /// <summary>The PhotoScope icon (all sizes), shared by every window's title bar and taskbar button.</summary>
    public static ImageSource? AppIcon { get; } = LoadIcon();

    static ImageSource? LoadIcon()
    {
        try
        {
            var frame = BitmapFrame.Create(new Uri("pack://application:,,,/PhotoScope;component/Assets/PhotoScope.ico"),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch
        {
            return null; // falls back to the exe's icon
        }
    }

    public ThemedWindow()
    {
        SetResourceReference(BackgroundProperty, "Brush.Window");
        SetResourceReference(ForegroundProperty, "Brush.Text");
        SetResourceReference(FontFamilyProperty, "Font.UI");
        FontSize = 14;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        Icon = AppIcon;
        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
        App.SendToBack(this);
    }
}
