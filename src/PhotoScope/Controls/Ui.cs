using System.Windows;
using System.Windows.Media;

namespace PhotoScope.Controls;

/// <summary>Attached properties used by the control templates in Styles.xaml.</summary>
public static class Ui
{
    public static readonly DependencyProperty HoverBrushProperty =
        DependencyProperty.RegisterAttached("HoverBrush", typeof(Brush), typeof(Ui), new PropertyMetadata(null));
    public static Brush? GetHoverBrush(DependencyObject d) => (Brush?)d.GetValue(HoverBrushProperty);
    public static void SetHoverBrush(DependencyObject d, Brush? v) => d.SetValue(HoverBrushProperty, v);

    public static readonly DependencyProperty PressedBrushProperty =
        DependencyProperty.RegisterAttached("PressedBrush", typeof(Brush), typeof(Ui), new PropertyMetadata(null));
    public static Brush? GetPressedBrush(DependencyObject d) => (Brush?)d.GetValue(PressedBrushProperty);
    public static void SetPressedBrush(DependencyObject d, Brush? v) => d.SetValue(PressedBrushProperty, v);

    public static readonly DependencyProperty CheckedBrushProperty =
        DependencyProperty.RegisterAttached("CheckedBrush", typeof(Brush), typeof(Ui), new PropertyMetadata(null));
    public static Brush? GetCheckedBrush(DependencyObject d) => (Brush?)d.GetValue(CheckedBrushProperty);
    public static void SetCheckedBrush(DependencyObject d, Brush? v) => d.SetValue(CheckedBrushProperty, v);

    public static readonly DependencyProperty CheckedForegroundProperty =
        DependencyProperty.RegisterAttached("CheckedForeground", typeof(Brush), typeof(Ui), new PropertyMetadata(null));
    public static Brush? GetCheckedForeground(DependencyObject d) => (Brush?)d.GetValue(CheckedForegroundProperty);
    public static void SetCheckedForeground(DependencyObject d, Brush? v) => d.SetValue(CheckedForegroundProperty, v);

    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.RegisterAttached("CornerRadius", typeof(CornerRadius), typeof(Ui), new PropertyMetadata(new CornerRadius(4)));
    public static CornerRadius GetCornerRadius(DependencyObject d) => (CornerRadius)d.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject d, CornerRadius v) => d.SetValue(CornerRadiusProperty, v);

    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.RegisterAttached("Placeholder", typeof(string), typeof(Ui), new PropertyMetadata(""));
    public static string GetPlaceholder(DependencyObject d) => (string)d.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject d, string v) => d.SetValue(PlaceholderProperty, v);

    /// <summary>A Segoe Fluent Icons glyph shown by templates that support it (menu items, nav items).</summary>
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.RegisterAttached("Icon", typeof(string), typeof(Ui), new PropertyMetadata(""));
    public static string GetIcon(DependencyObject d) => (string)d.GetValue(IconProperty);
    public static void SetIcon(DependencyObject d, string v) => d.SetValue(IconProperty, v);
}
