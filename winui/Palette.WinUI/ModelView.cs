using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace Palette;

/// <summary>
/// Interactive preview of 3D models (STEP, STL, 3MF, OBJ, PLY, glTF). Sight, the viewer, runs
/// beside the palette, draws the model into an offscreen buffer and streams each frame to a picture
/// on the preview stage; the mouse over the stage is sent back to it (drag to rotate, right-drag or
/// Shift-drag to pan, wheel to zoom, double-click to reset). Only the selected row's model moves;
/// keys stay with the search box. See Embed in Sight's app.rs for the protocol.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly HashSet<string> ModelExt = new(StringComparer.OrdinalIgnoreCase)
        { ".step", ".stp", ".stl", ".3mf", ".obj", ".ply", ".glb", ".gltf" };

    private Process? _sight;
    private StreamWriter? _sightIn;
    private string? _modelPath;       // the model the stage is meant to show
    private string? _modelSent;       // the one Sight was last told to open
    private (int w, int h) _modelSize;
    private WriteableBitmap? _modelBitmap;
    private (int w, int h, bool ready, byte[] px)? _modelFrame;   // newest frame, not yet shown
    private readonly object _frameLock = new();
    private bool _stageDrag, _stagePan;
    private Windows.Foundation.Point _stageLast;

    private static string? SightExe()
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "sight.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "palette", "sight.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private bool SightSend(string line)
    {
        try
        {
            _sightIn!.WriteLine(line);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool EnsureSight()
    {
        if (_sight is { HasExited: false } && _sightIn != null) return true;
        _sight = null;
        _sightIn = null;
        _modelSent = null;
        _modelSize = default;
        string? exe = SightExe();
        if (exe == null) return false;
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                StandardInputEncoding = new UTF8Encoding(false),
            };
            psi.ArgumentList.Add("--embed");
            var proc = Process.Start(psi);
            if (proc == null) return false;
            _sight = proc;
            _sightIn = proc.StandardInput;
            _sightIn.AutoFlush = true;
            var output = proc.StandardOutput.BaseStream;
            new Thread(() => ReadFrames(proc, output)) { IsBackground = true, Name = "sight-frames" }.Start();
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"sight start failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Frames from Sight: "SGHT", width, height, flags, then BGRA pixels. Only the newest is kept.</summary>
    private void ReadFrames(Process proc, Stream s)
    {
        var head = new byte[16];
        try
        {
            while (true)
            {
                s.ReadExactly(head);
                if (head[0] != 'S' || head[1] != 'G' || head[2] != 'H' || head[3] != 'T') return;
                int w = BitConverter.ToInt32(head, 4), h = BitConverter.ToInt32(head, 8);
                bool ready = (BitConverter.ToUInt32(head, 12) & 1) != 0;
                if (w <= 0 || h <= 0 || w > 8192 || h > 8192) return;
                var px = new byte[w * h * 4];
                s.ReadExactly(px);
                bool first;
                lock (_frameLock)
                {
                    first = _modelFrame == null;
                    _modelFrame = (w, h, ready, px);
                }
                if (first) DispatcherQueue.TryEnqueue(ShowModelFrame);
            }
        }
        catch
        {
            // Sight ended; the next preview starts a new one.
        }
    }

    private void ShowModelFrame()
    {
        (int w, int h, bool ready, byte[] px)? f;
        lock (_frameLock)
        {
            f = _modelFrame;
            _modelFrame = null;
        }
        if (f is not { } fr) return;
        if (!fr.ready || _modelPath == null)
        {
            ModelFrame.Visibility = Visibility.Collapsed;
            return;
        }
        if (_modelBitmap == null || _modelBitmap.PixelWidth != fr.w || _modelBitmap.PixelHeight != fr.h)
        {
            _modelBitmap = new WriteableBitmap(fr.w, fr.h);
            ModelFrame.Source = _modelBitmap;
        }
        using (var st = _modelBitmap.PixelBuffer.AsStream()) st.Write(fr.px, 0, fr.px.Length);
        _modelBitmap.Invalidate();
        ModelFrame.Visibility = Visibility.Visible;
    }

    /// <summary>The stage shows <paramref name="path"/> as soon as Sight has drawn it.</summary>
    private void StartModelView(string path)
    {
        if (!EnsureSight()) return;
        if (_modelPath != path) ModelFrame.Visibility = Visibility.Collapsed;
        _modelPath = path;
        SendStageSize();
        if (_modelSent != path)
        {
            bool dark = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
            SightSend(dark ? "dark" : "light");
            SightSend("open " + path);
            _modelSent = path;
        }
    }

    private void SendStageSize()
    {
        double s = Scale;
        int w = (int)Math.Round(PreviewStage.ActualWidth * s), h = (int)Math.Round(PreviewStage.ActualHeight * s);
        if (w < 16 || h < 16 || (w, h) == _modelSize || _sightIn == null) return;
        _modelSize = (w, h);
        SightSend(string.Create(CultureInfo.InvariantCulture, $"size {w} {h} {s}"));
    }

    private void OnStageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_modelPath != null) SendStageSize();
    }

    /// <summary>Forget the model: its picture goes away; Sight stays up for the next one.</summary>
    private void StopModelView()
    {
        _modelPath = null;
        _stageDrag = false;
        ModelFrame.Visibility = Visibility.Collapsed;
    }

    private void OnStagePressed(object sender, PointerRoutedEventArgs e)
    {
        if (_modelPath == null) return;
        var pt = e.GetCurrentPoint(PreviewStage);
        _stageLast = pt.Position;
        _stagePan = pt.Properties.IsRightButtonPressed || pt.Properties.IsMiddleButtonPressed
            || (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0;
        _stageDrag = true;
        PreviewStage.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnStageMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_stageDrag || _modelPath == null) return;
        var p = e.GetCurrentPoint(PreviewStage).Position;
        double dx = p.X - _stageLast.X, dy = p.Y - _stageLast.Y;
        _stageLast = p;
        SightSend(string.Create(CultureInfo.InvariantCulture, $"{(_stagePan ? "pan" : "orbit")} {dx:F2} {dy:F2}"));
        e.Handled = true;
    }

    private void OnStageReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_stageDrag) return;
        _stageDrag = false;
        PreviewStage.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnStageWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_modelPath == null) return;   // other previews let the list scroll
        double steps = e.GetCurrentPoint(PreviewStage).Properties.MouseWheelDelta / 120.0;
        SightSend(string.Create(CultureInfo.InvariantCulture, $"wheel {steps:F2}"));
        e.Handled = true;
    }

    private void OnStageDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_modelPath == null) return;   // otherwise the pane opens the file
        SightSend("reset");
        e.Handled = true;
    }
}
