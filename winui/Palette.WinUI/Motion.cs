using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Palette;

/// <summary>
/// Motion that keeps things from jumping:
/// <list type="bullet">
/// <item>The selection card (and its accent pill) is one element that glides to the selected row,
/// rather than each row lighting up on its own. It sits in the list's header, so it scrolls with
/// the rows and is drawn beneath them.</item>
/// <item>A new view (actions, all apps, done, back to results) fades and slides in.</item>
/// <item>Moving the selection off the visible rows scrolls there smoothly.</item>
/// </list>
/// The opening pop, the preview and the panel's height are animated in MainWindow.xaml.cs.
/// </summary>
public sealed partial class MainWindow
{
    private const double HighlightMs = 170;    // the glide from one row to the next
    private double _hlX, _hlY, _hlW, _hlH;     // where the card is, in header coordinates
    private double _hlFromX, _hlFromY, _hlFromW, _hlFromH;   // where the glide started
    private readonly System.Diagnostics.Stopwatch _hlClock = new();
    private HitVm? _hlFor;                     // the row it is on or heading to
    private bool _hlTravel;                    // gliding to a newly selected row
    private bool _hlSnap = true;               // the next placement jumps (nothing on screen to glide from)

    private void InitMotion()
    {
        // Rows change size (a preview opening) and move (results updating) without the selection
        // changing: follow them.
        ResultsList.LayoutUpdated += (_, _) => UpdateHighlight();
    }

    /// <summary>The selected row's box in header coordinates, or false when nothing shows a card.</summary>
    private bool HighlightTarget(out Windows.Foundation.Rect r)
    {
        r = default;
        int i = ResultsList.SelectedIndex;
        if (!_visible || i < 0 || i >= Items.Count || Items[i].IsStatus) return false;
        if (ResultsList.ContainerFromIndex(i) is not ListViewItem c || c.ActualHeight <= 0) return false;
        var p = c.TransformToVisual(HlCanvas).TransformPoint(default);
        r = new Windows.Foundation.Rect(p.X, p.Y, c.ActualWidth, c.ActualHeight);
        return true;
    }

    /// <summary>Place the card for the current selection: jump, follow, or start a glide.</summary>
    private void UpdateHighlight()
    {
        if (!HighlightTarget(out var r))
        {
            SelHighlight.Opacity = 0;
            _hlSnap = true;
            _hlTravel = false;
            _hlFor = null;
            return;
        }
        var vm = Items[ResultsList.SelectedIndex];
        if (vm != _hlFor)
        {
            _hlFor = vm;
            _hlTravel = !_hlSnap && AnimationsOn;
            (_hlFromX, _hlFromY, _hlFromW, _hlFromH) = (_hlX, _hlY, _hlW, _hlH);
            _hlClock.Restart();
        }
        _hlSnap = false;
        if (_hlTravel) EnsureFrames();   // OnFrame moves it
        else PlaceHighlight(r.X, r.Y, r.Width, r.Height);
        SelHighlight.Opacity = 1;
    }

    /// <summary>One frame of the glide; true while it is still moving.</summary>
    private bool StepHighlight(double dt)
    {
        if (!_hlTravel) return false;
        if (!HighlightTarget(out var r))
        {
            UpdateHighlight();
            return false;
        }
        // Aim where the row will settle: rows above it may still be closing a preview's room, and
        // chasing its moving position would make the card overshoot and come back.
        double t = Math.Min(1, _hlClock.Elapsed.TotalMilliseconds / HighlightMs);
        double e = 1 - Math.Pow(1 - t, 3);
        double ty = r.Y - ClosingAbove(ResultsList.SelectedIndex);
        bool done = t >= 1;
        if (done) _hlTravel = false;
        PlaceHighlight(
            _hlFromX + (r.X - _hlFromX) * e,
            done ? r.Y : _hlFromY + (ty - _hlFromY) * e,
            _hlFromW + (r.Width - _hlFromW) * e,
            _hlFromH + (r.Height - _hlFromH) * e);
        return !done;
    }

    private void PlaceHighlight(double x, double y, double w, double h)
    {
        (_hlX, _hlY, _hlW, _hlH) = (x, y, w, h);
        if (Canvas.GetLeft(SelHighlight) != x) Canvas.SetLeft(SelHighlight, x);
        if (Canvas.GetTop(SelHighlight) != y) Canvas.SetTop(SelHighlight, y);
        if (SelHighlight.Width != w) SelHighlight.Width = w;
        if (SelHighlight.Height != h) SelHighlight.Height = h;
    }

    // ------------------------------------------------------------ view changes

    private Storyboard? _listIn;

    /// <summary>
    /// The list's new contents fade in, sliding up when going into something (actions, all apps,
    /// done) and down when coming back to results.
    /// </summary>
    private void PlayListEntrance(bool forward)
    {
        _hlSnap = true;   // the card arrives with the rows rather than flying across
        if (!_visible || !AnimationsOn) return;
        _listIn?.Stop();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(170), EasingFunction = ease };
        var move = new DoubleAnimation { From = forward ? 12 : -12, To = 0, Duration = TimeSpan.FromMilliseconds(240), EasingFunction = ease };
        Storyboard.SetTarget(fade, ResultsList);
        Storyboard.SetTargetProperty(fade, "Opacity");
        Storyboard.SetTarget(move, ListXf);
        Storyboard.SetTargetProperty(move, "Y");
        _listIn = new Storyboard();
        _listIn.Children.Add(fade);
        _listIn.Children.Add(move);
        _listIn.Begin();
    }

    /// <summary>Forget any entrance in flight (the palette is opening afresh).</summary>
    private void ResetMotion()
    {
        _listIn?.Stop();
        _listIn = null;
        _hlSnap = true;
        _hlTravel = false;
        _hlFor = null;
        SelHighlight.Opacity = 0;
    }

    // ------------------------------------------------------------ scrolling

    private ScrollViewer? _listScroll;

    /// <summary>Bring the selected row into view, scrolling smoothly when it is already laid out.</summary>
    private void ScrollToSelection()
    {
        if (ResultsList.SelectedItem is not { } item) return;
        _listScroll ??= FindScrollViewer(ResultsList);
        if (_listScroll is not { } sv || !_visible || !AnimationsOn
            || ResultsList.ContainerFromItem(item) is not FrameworkElement c || c.ActualHeight <= 0)
        {
            ResultsList.ScrollIntoView(item);
            return;
        }
        double top = c.TransformToVisual(sv).TransformPoint(default).Y, bottom = top + c.ActualHeight;
        double off = sv.VerticalOffset;
        if (top < 0) off += top - 2;
        else if (bottom > sv.ViewportHeight) off += bottom - sv.ViewportHeight + 2;
        else return;
        sv.ChangeView(null, Math.Max(0, off), null, disableAnimation: false);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var o = stack.Pop();
            if (o is ScrollViewer sv) return sv;
            int n = VisualTreeHelper.GetChildrenCount(o);
            for (int i = 0; i < n; i++) stack.Push(VisualTreeHelper.GetChild(o, i));
        }
        return null;
    }
}
