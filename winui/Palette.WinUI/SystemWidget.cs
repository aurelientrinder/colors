using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Palette;

/// <summary>
/// The live system card: "cpu", "gpu", "memory", "disk" or "network" puts a row on top with the
/// reading now and a graph of the last minute, like the weather and stock cards. Sampling
/// (<see cref="SystemStats"/>) runs only while the card is showing.
/// </summary>
public sealed partial class MainWindow
{
    private void ShowSystemRow(SystemAnswer answer)
    {
        int sel = ResultsList.SelectedIndex;
        Items.Insert(0, HitVm.ForSystem(answer));
        if (sel <= 0) ResultsList.SelectedIndex = 0;
        else _expandedQuery = _latestQuery;
        UpdateCondensed();
        Resize(recenter: false);
        UpdatePreview();
        UpdateOpenButton();
    }

    private void RenderSystem(SystemAnswer a)
    {
        SysTitle.Text = a.Kind switch
        {
            SysKind.Cpu => "CPU",
            SysKind.Gpu => "GPU",
            SysKind.Npu => "NPU",
            SysKind.Memory => "Memory",
            SysKind.Disk => "Disk",
            _ => "Network",
        };
        SysValue.Text = "";
        SysDetail.Text = "";
        DrawSystem(a.Kind);
        SystemStats.Start(a.Kind, () => DispatcherQueue.TryEnqueue(OnSystemSample));
    }

    /// <summary>A new sample arrived. Stops itself once the card is no longer in front.</summary>
    private void OnSystemSample()
    {
        if (!_visible || _pvVm?.Answer is not SystemAnswer a || SystemPreview.Visibility != Visibility.Visible)
        {
            SystemStats.Stop();
            return;
        }
        DrawSystem(a.Kind);
    }

    private void OnSystemGraphSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_pvVm?.Answer is SystemAnswer a) DrawSystem(a.Kind);
    }

    private void DrawSystem(SysKind kind)
    {
        var series = SystemStats.Series(kind);
        bool percent = kind != SysKind.Network;
        if (series.Length > 0)
        {
            double now = series[^1];
            SysValue.Text = percent
                ? $"{now:0}%"
                : now >= 100 ? $"{now:0} Mbps" : now >= 1 ? $"{now:0.0} Mbps" : $"{now * 1000:0} kbps";
            SysDetail.Text = SystemStats.DetailOf(kind);
        }
        else
        {
            SysDetail.Text = "Reading…";
        }

        double w = SysGraph.ActualWidth, h = SysGraph.ActualHeight;
        var line = new PointCollection();
        var fill = new PointCollection();
        if (w > 0 && h > 0 && series.Length > 0)
        {
            double max = percent ? 100 : Math.Max(1, series.Max() * 1.2);
            double step = w / (SystemStats.Points - 1);
            double x0 = w - (series.Length - 1) * step;   // the newest sample sits at the right edge
            double Y(double v) => h - 2 - Math.Clamp(v / max, 0, 1) * (h - 4);
            fill.Add(new Point(x0, h));
            for (int i = 0; i < series.Length; i++)
            {
                var p = new Point(x0 + i * step, Y(series[i]));
                line.Add(p);
                fill.Add(p);
            }
            fill.Add(new Point(w, h));
        }
        SysLine.Points = line;
        SysFill.Points = fill;
        SysMid.X1 = 0;
        SysMid.X2 = w;
        SysMid.Y1 = SysMid.Y2 = h / 2;
    }
}
