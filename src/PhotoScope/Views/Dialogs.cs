using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using PhotoScope.Controls;

namespace PhotoScope.Views;

public sealed record DialogButton(string Id, string Label, bool IsAccent = false, bool IsCancel = false, bool IsDefault = false);

/// <summary>A small themed message / confirmation / text-input dialog built in code.</summary>
public sealed class MessageDialog : ThemedWindow
{
    readonly TextBox? _input;
    readonly TextBlock? _error;
    readonly Func<string, string?>? _validate;

    public string? Clicked { get; private set; }
    public string InputText => _input?.Text ?? "";

    public MessageDialog(string title, string message, IReadOnlyList<DialogButton> buttons,
        string? input = null, Func<string, string?>? validate = null, int selectLength = -1)
    {
        Title = title;
        SizeToContent = SizeToContent.Height;
        Width = 460;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _validate = validate;

        var root = new DockPanel { LastChildFill = true };

        // Footer with buttons.
        var footer = new Border { Padding = new Thickness(24, 16, 24, 20), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BackgroundProperty, "Brush.Window");
        footer.SetResourceReference(Border.BorderBrushProperty, "Brush.Divider");
        var buttonGrid = new UniformGrid { Rows = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var b in buttons)
        {
            var button = new Button
            {
                Content = b.Label,
                Margin = new Thickness(4, 0, 4, 0),
                IsCancel = b.IsCancel,
                IsDefault = b.IsDefault,
                Tag = b.Id,
            };
            if (b.IsAccent) button.SetResourceReference(StyleProperty, "AccentButton");
            button.Click += (_, _) => OnButton(b);
            buttonGrid.Children.Add(button);
        }
        footer.Child = buttonGrid;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // Body.
        var body = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        body.SetResourceReference(Panel.BackgroundProperty, "Brush.Card");
        var titleText = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        titleText.SetResourceReference(StyleProperty, "Text.Subtitle");
        titleText.FontSize = 18;
        body.Children.Add(titleText);
        if (!string.IsNullOrEmpty(message))
        {
            body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 20 });
        }
        if (input != null)
        {
            _input = new TextBox { Text = input, Margin = new Thickness(0, 14, 0, 0) };
            _input.TextChanged += (_, _) => Validate();
            body.Children.Add(_input);
            _error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
            _error.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
            body.Children.Add(_error);
            Loaded += (_, _) =>
            {
                _input.Focus();
                if (selectLength >= 0) _input.Select(0, Math.Min(selectLength, _input.Text.Length));
                else _input.SelectAll();
            };
        }
        var bodyHost = new Border { Child = body };
        bodyHost.SetResourceReference(Border.BackgroundProperty, "Brush.Card");
        root.Children.Add(bodyHost);

        Content = root;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Clicked = null; DialogResult = false; }
        };
    }

    string? Validate()
    {
        if (_input == null || _error == null || _validate == null) return null;
        var message = _validate(_input.Text);
        _error.Text = message ?? "";
        _error.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
        return message;
    }

    void OnButton(DialogButton b)
    {
        if (!b.IsCancel && _input != null && Validate() != null) return;
        Clicked = b.IsCancel ? null : b.Id;
        DialogResult = !b.IsCancel;
    }
}

/// <summary>Convenience wrappers so view models can ask simple questions.</summary>
public static class Dialogs
{
    /// <summary>A visible window to own a dialog: the active one, else the visible main window, else none.</summary>
    public static Window? Owner()
    {
        var app = Application.Current;
        if (app == null) return null;
        foreach (Window w in app.Windows)
            if (w.IsActive && w.IsVisible) return w;
        return app.MainWindow is { IsVisible: true } m ? m : null;
    }

    public static string? Show(string title, string message, params DialogButton[] buttons)
    {
        var dlg = new MessageDialog(title, message, buttons) { Owner = Owner() };
        if (dlg.Owner == null) dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dlg.ShowDialog();
        return dlg.Clicked;
    }

    public static bool Confirm(string title, string message, string confirmLabel, string cancelLabel = "Cancel") =>
        Show(title, message,
            new DialogButton("ok", confirmLabel, IsAccent: true, IsDefault: true),
            new DialogButton("cancel", cancelLabel, IsCancel: true)) == "ok";

    public static void Info(string title, string message) =>
        Show(title, message, new DialogButton("ok", "OK", IsAccent: true, IsDefault: true, IsCancel: true));

    public static void Error(string title, string message) => Info(title, message);

    public static string? Prompt(string title, string message, string initial, Func<string, string?> validate,
        string okLabel = "OK", int selectLength = -1)
    {
        var dlg = new MessageDialog(title, message,
            [new DialogButton("ok", okLabel, IsAccent: true, IsDefault: true), new DialogButton("cancel", "Cancel", IsCancel: true)],
            initial, validate, selectLength)
        { Owner = Owner() };
        if (dlg.Owner == null) dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dlg.ShowDialog() == true && dlg.Clicked == "ok" ? dlg.InputText : null;
    }
}
