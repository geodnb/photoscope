using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PhotoScope.Controls;

/// <summary>Five clickable stars. Clicking the current rating again clears it. Executes <see cref="Command"/> with the new value.</summary>
public sealed class StarRating : StackPanel
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(StarRating), new PropertyMetadata(0, (d, _) => ((StarRating)d).Refresh()));

    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(
        nameof(Command), typeof(ICommand), typeof(StarRating));

    public int Value
    {
        get => (int)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public StarRating()
    {
        Orientation = Orientation.Horizontal;
        for (var i = 1; i <= 5; i++)
        {
            var n = i;
            var b = new Button
            {
                Width = 28,
                Height = 28,
                MinHeight = 0,
                Padding = new Thickness(0),
                FontSize = 15,
                Tag = n,
                ToolTip = n == 1 ? "1 star" : $"{n} stars",
            };
            b.SetResourceReference(StyleProperty, "IconButton");
            AutomationProperties.SetName(b, n == 1 ? "1 star" : $"{n} stars");
            b.Click += (_, _) =>
            {
                var value = Value == n ? 0 : n;
                if (Command?.CanExecute(value) == true) Command.Execute(value);
            };
            Children.Add(b);
        }
        Refresh();
    }

    void Refresh()
    {
        foreach (Button b in Children)
        {
            var filled = (int)b.Tag <= Value;
            b.Content = filled ? "\uE735" : "\uE734";
            if (filled) b.Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xA6, 0x00));
            else b.SetResourceReference(Control.ForegroundProperty, "Brush.TextSecondary");
        }
    }
}
