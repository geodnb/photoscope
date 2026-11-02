using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using PhotoScope.Models;

namespace PhotoScope.Controls;

/// <summary>
/// Drag source behaviour for a multi-select ListBox. Clicking an already-selected item doesn't
/// collapse the selection until the mouse is released without dragging, so several photos can be
/// dragged at once.
/// </summary>
public sealed class ListDragSource
{
    readonly ListBox _list;
    readonly string _format;
    Point? _start;
    ListBoxItem? _pressed;
    bool _deferredSelect;

    public ListDragSource(ListBox list, string format)
    {
        _list = list;
        _format = format;
        list.PreviewMouseLeftButtonDown += OnDown;
        list.PreviewMouseMove += OnMove;
        list.PreviewMouseLeftButtonUp += OnUp;
    }

    public static ListBoxItem? ItemAt(ItemsControl list, DependencyObject? source)
    {
        if (source is not Visual and not System.Windows.Media.Media3D.Visual3D) return null;
        return ItemsControl.ContainerFromElement(list, source) as ListBoxItem;
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = null;
        _deferredSelect = false;
        if (e.ClickCount > 1) return;
        _pressed = ItemAt(_list, e.OriginalSource as DependencyObject);
        if (_pressed == null) return;
        // Clicks on buttons inside an item (e.g. "remove") aren't drags.
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        _start = e.GetPosition(_list);

        if (_pressed.IsSelected && _list.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            _deferredSelect = true;
            _pressed.Focus();
            e.Handled = true;
        }
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (_start is not { } start || e.LeftButton != MouseButtonState.Pressed || _pressed == null) return;
        var p = e.GetPosition(_list);
        if (Math.Abs(p.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _start = null;
        _deferredSelect = false;
        if (!_pressed.IsSelected) _list.SelectedItem = _pressed.DataContext;
        var photos = _list.SelectedItems.Cast<object>().OfType<Photo>()
            .OrderBy(x => _list.Items.IndexOf(x)).ToList();
        if (photos.Count == 0) return;

        var data = new DataObject(_format, photos.Select(x => x.Id).ToArray());
        try
        {
            DragDrop.DoDragDrop(_list, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            Services.Log.Warn("Drag and drop failed", ex);
        }
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferredSelect && _pressed != null)
        {
            _list.SelectedItems.Clear();
            _pressed.IsSelected = true;
        }
        _deferredSelect = false;
        _start = null;
        _pressed = null;
    }

    static bool IsInsideButton(DependencyObject? d)
    {
        for (; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase) return true;
            if (d is ListBoxItem) return false;
        }
        return false;
    }
}

/// <summary>Finds where in a horizontal or wrapping list a drop should insert, and draws a marker there.</summary>
public sealed class InsertionMarker : Adorner
{
    double _x, _top, _height;

    InsertionMarker(UIElement adorned) : base(adorned) => IsHitTestVisible = false;

    public static InsertionMarker Attach(UIElement list)
    {
        var marker = new InsertionMarker(list);
        AdornerLayer.GetAdornerLayer(list)?.Add(marker);
        return marker;
    }

    public void Detach() => AdornerLayer.GetAdornerLayer(AdornedElement)?.Remove(this);

    /// <summary>Returns the insert index for <paramref name="point"/> (in list coordinates) and moves the marker.</summary>
    public static int IndexAt(ItemsControl list, Point point, InsertionMarker? marker)
    {
        var count = list.Items.Count;
        ListBoxItem? best = null;
        var bestIndex = count;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem c || !c.IsVisible) continue;
            var topLeft = c.TranslatePoint(new Point(0, 0), list);
            var centre = new Point(topLeft.X + c.ActualWidth / 2, topLeft.Y + c.ActualHeight / 2);
            var d = Math.Abs(centre.X - point.X) + Math.Abs(centre.Y - point.Y) * 2;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = c;
                bestIndex = point.X > centre.X ? i + 1 : i;
            }
        }
        if (marker != null)
        {
            if (best != null)
            {
                var topLeft = best.TranslatePoint(new Point(0, 0), list);
                var after = bestIndex > list.ItemContainerGenerator.IndexFromContainer(best);
                marker._x = after ? topLeft.X + best.ActualWidth : topLeft.X;
                marker._top = topLeft.Y + 4;
                marker._height = best.ActualHeight - 8;
            }
            else
            {
                marker._x = 8;
                marker._top = 8;
                marker._height = Math.Max(0, ((FrameworkElement)list).ActualHeight - 16);
            }
            marker.InvalidateVisual();
        }
        return bestIndex;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var brush = (Brush?)TryFindResource("Brush.Accent") ?? Brushes.DodgerBlue;
        dc.DrawRoundedRectangle(brush, null, new Rect(_x - 1.5, _top, 3, Math.Max(0, _height)), 1.5, 1.5);
    }
}
