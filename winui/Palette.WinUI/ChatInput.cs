using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Palette;

/// <summary>
/// The message box in the chat: a long message wraps instead of running off the right, and the search card
/// grows a line at a time to hold it (up to <see cref="InputMaxLines"/> lines, then the box scrolls). In
/// every other view the field is the one-line search it always was.
/// </summary>
public sealed partial class MainWindow
{
    private const double InputMaxLines = 6, SearchCardDip = 100;
    private double _inputExtraDip;    // how much taller than one line the message box is
    private double _inputLineDip;     // the box's height on one line, measured

    private void InitInputGrowth()
    {
        Input.SizeChanged += (_, e) =>
        {
            if (Input.TextWrapping == TextWrapping.NoWrap || _inputLineDip <= 0)
            {
                if (Input.TextWrapping == TextWrapping.NoWrap) _inputLineDip = e.NewSize.Height;
                return;
            }
            GrowSearchCard(Math.Max(0, e.NewSize.Height - _inputLineDip));
        };
    }

    /// <summary>Wrap the field (in the chat) or keep it on one line (everywhere else).</summary>
    private void WrapInput(bool wrap)
    {
        if (wrap)
        {
            Input.TextWrapping = TextWrapping.Wrap;
            // Six lines, then it scrolls (a line is the field's one-line height less its padding).
            double line = Input.FontSize * 1.43;
            Input.MaxHeight = _inputLineDip > 0 ? _inputLineDip + (InputMaxLines - 1) * line : double.PositiveInfinity;
        }
        else
        {
            Input.TextWrapping = TextWrapping.NoWrap;
            Input.MaxHeight = double.PositiveInfinity;
            GrowSearchCard(0);
        }
    }

    /// <summary>Make the search card <paramref name="extra"/> taller than its one line, and the panel with it.</summary>
    private void GrowSearchCard(double extra)
    {
        extra = Math.Round(extra);
        if (Math.Abs(extra - _inputExtraDip) < 0.5) return;
        _inputExtraDip = extra;
        SearchCard.Height = SearchCardDip + extra;
        if (!_animating) ApplyShape();
    }
}

/// <summary>A grid that shows the hand under the pointer: it is clicked (a chip, a step, a card).</summary>
internal sealed class HandGrid : Grid
{
    public HandGrid() => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
}

/// <summary>A panel that shows the hand under the pointer: it is clicked (a file's link, a step's line).</summary>
internal sealed class HandPanel : StackPanel
{
    public HandPanel() => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
}
