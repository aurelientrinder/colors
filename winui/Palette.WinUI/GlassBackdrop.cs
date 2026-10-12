using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Palette;

/// <summary>
/// Thin desktop acrylic (the more see-through variant Windows 11 uses for flyouts), kept in its
/// active look even when the window is not focused, and following the light/dark theme.
/// </summary>
internal sealed class GlassBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _config;
    private FrameworkElement? _content;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        _content = root.Content as FrameworkElement;
        _config = new SystemBackdropConfiguration { IsInputActive = true };
        ApplyTheme();
        if (_content != null) _content.ActualThemeChanged += OnThemeChanged;

        _acrylic = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Thin };
        _acrylic.AddSystemBackdropTarget(target);
        _acrylic.SetSystemBackdropConfiguration(_config);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        if (_content != null) _content.ActualThemeChanged -= OnThemeChanged;
        _acrylic?.RemoveSystemBackdropTarget(target);
        _acrylic?.Dispose();
        _acrylic = null;
    }

    private void OnThemeChanged(FrameworkElement sender, object args) => ApplyTheme();

    private void ApplyTheme()
    {
        if (_config == null) return;
        _config.Theme = _content?.ActualTheme switch
        {
            ElementTheme.Dark => SystemBackdropTheme.Dark,
            ElementTheme.Light => SystemBackdropTheme.Light,
            _ => SystemBackdropTheme.Default,
        };
    }
}
