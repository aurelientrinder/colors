using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Palette;

public enum SysKind { Cpu, Gpu, Npu, Memory, Disk, Network }

/// <summary>A live system reading in the list: "cpu", "gpu", "memory", "disk", "network".</summary>
public sealed record SystemAnswer(string Query, SysKind Kind) : WebAnswers.Answer(Query);

/// <summary>
/// Samples the machine once a second while a system card is on screen, and keeps the last minute
/// of each reading for its graph. Nothing runs otherwise. Percent readings are 0 to 100; network
/// is megabits per second of the busiest adapter.
/// </summary>
internal static class SystemStats
{
    public const int Points = 60;

    private static readonly object Gate = new();
    private static readonly Dictionary<SysKind, Queue<double>> History = new();
    private static readonly Dictionary<SysKind, string> Detail = new();
    private static Timer? _timer;
    private static SysKind _wanted;
    private static Action? _changed;
    private static int _busy;

    // CPU
    private static ulong _idle0, _total0;
    // counters
    private static PerformanceCounter? _diskIdle;
    private static readonly Dictionary<string, CounterSample> _gpuPrev = new();
    private static readonly Dictionary<string, CounterSample> _netPrev = new();
    private static bool _gpuMissing;

    /// <summary>What a typed query asks to watch, or null. Whole queries only: "cpu" is not "cpu-z".</summary>
    public static SysKind? Detect(string query)
    {
        string q = string.Join(' ', query.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return q switch
        {
            "cpu" or "cpu usage" or "cpu load" or "cpu graph" or "processor" or "processor usage" => SysKind.Cpu,
            "gpu" or "gpu usage" or "gpu load" or "gpu graph" or "graphics usage" => SysKind.Gpu,
            "npu" or "npu usage" or "npu load" or "npu graph" or "ai boost" or "neural engine" => SysKind.Npu,
            "memory" or "memory usage" or "ram" or "ram usage" or "memory graph" => SysKind.Memory,
            "disk" or "disk usage" or "disk activity" or "disk graph" => SysKind.Disk,
            "network" or "network usage" or "network activity" or "network speed" or "bandwidth" => SysKind.Network,
            _ => null,
        };
    }

    /// <summary>Begin sampling (idempotent). <paramref name="changed"/> runs on a pool thread after each sample.</summary>
    public static void Start(SysKind kind, Action changed)
    {
        lock (Gate)
        {
            _wanted = kind;
            _changed = changed;
            if (_timer != null) return;
            _idle0 = _total0 = 0;
            _timer = new Timer(_ => Tick(), null, 0, 1000);
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _timer?.Dispose();
            _timer = null;
            _changed = null;
            _gpuPrev.Clear();
            _netPrev.Clear();
            _diskIdle?.Dispose();
            _diskIdle = null;
        }
    }

    public static double[] Series(SysKind kind)
    {
        lock (Gate) return History.TryGetValue(kind, out var q) ? q.ToArray() : Array.Empty<double>();
    }

    public static string DetailOf(SysKind kind)
    {
        lock (Gate) return Detail.GetValueOrDefault(kind, "");
    }

    private static void Add(SysKind kind, double v, string detail)
    {
        lock (Gate)
        {
            if (!History.TryGetValue(kind, out var q)) History[kind] = q = new Queue<double>();
            q.Enqueue(v);
            while (q.Count > Points) q.Dequeue();
            Detail[kind] = detail;
        }
    }

    private static void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;   // the last sample is still being read
        try
        {
            SysKind kind;
            Action? changed;
            lock (Gate)
            {
                kind = _wanted;
                changed = _changed;
            }
            if (changed == null) return;
            switch (kind)
            {
                case SysKind.Cpu: SampleCpu(); break;
                case SysKind.Memory: SampleMemory(); break;
                case SysKind.Disk: SampleDisk(); break;
                case SysKind.Gpu: SampleEngines(SysKind.Gpu); break;
                case SysKind.Npu: SampleEngines(SysKind.Npu); break;
                case SysKind.Network: SampleNetwork(); break;
            }
            changed();
        }
        catch (Exception ex)
        {
            App.Log("system sample failed: " + ex.Message);
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    // ---- CPU ---------------------------------------------------------------------------------

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    private static void SampleCpu()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user)) return;
        ulong total = (ulong)(kernel + user), idleT = (ulong)idle;   // kernel time includes idle
        if (_total0 != 0 && total > _total0)
        {
            double busy = 100.0 * (1 - (double)(idleT - _idle0) / (total - _total0));
            Add(SysKind.Cpu, Math.Clamp(busy, 0, 100), $"{Environment.ProcessorCount} logical processors");
        }
        _idle0 = idleT;
        _total0 = total;
    }

    // ---- memory ------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    private static void SampleMemory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return;
        const double GB = 1024.0 * 1024 * 1024;
        double used = (m.ullTotalPhys - m.ullAvailPhys) / GB, all = m.ullTotalPhys / GB;
        Add(SysKind.Memory, m.dwMemoryLoad, string.Format(CultureInfo.CurrentCulture, "{0:0.0} of {1:0.0} GB in use", used, all));
    }

    // ---- disk --------------------------------------------------------------------------------

    private static void SampleDisk()
    {
        if (_diskIdle == null)
        {
            _diskIdle = new PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total", readOnly: true);
            _diskIdle.NextValue();   // the first reading of a rate is always 0
            return;
        }
        double idle = _diskIdle.NextValue();
        Add(SysKind.Disk, Math.Clamp(100 - idle, 0, 100), "Active time, all disks");
    }

    // ---- GPU and NPU ---------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct KmtAdapterInfo { public uint Handle; public int LuidLow, LuidHigh; public uint Sources; public int Preferred; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KmtEnum { public ulong Filter; public uint Count; public IntPtr Adapters; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KmtQuery { public uint Handle; public int Type; public IntPtr Data; public uint Size; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KmtClose { public uint Handle; }

    [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters3(ref KmtEnum e);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref KmtQuery q);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref KmtClose c);

    private static HashSet<string>? _npuLuids;

    /// <summary>
    /// The adapters that only compute (no graphics): the NPU. They appear in the same "GPU Engine"
    /// counters as the GPU, by LUID, so the two are told apart here. Names match the counters'
    /// instance names ("0x00000000_0x00014B72").
    /// </summary>
    private static HashSet<string> NpuLuids()
    {
        if (_npuLuids != null) return _npuLuids;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var e = new KmtEnum { Filter = 3 };   // include compute-only and display-only adapters
            if (D3DKMTEnumAdapters3(ref e) == 0 && e.Count > 0)
            {
                int size = Marshal.SizeOf<KmtAdapterInfo>();
                e.Adapters = Marshal.AllocHGlobal(size * (int)e.Count);
                try
                {
                    if (D3DKMTEnumAdapters3(ref e) == 0)
                    {
                        for (int i = 0; i < e.Count; i++)
                        {
                            var a = Marshal.PtrToStructure<KmtAdapterInfo>(e.Adapters + i * size);
                            IntPtr buf = Marshal.AllocHGlobal(4);
                            Marshal.WriteInt32(buf, 0);
                            var q = new KmtQuery { Handle = a.Handle, Type = 15 /* ADAPTERTYPE */, Data = buf, Size = 4 };
                            if (D3DKMTQueryAdapterInfo(ref q) == 0 && ((uint)Marshal.ReadInt32(buf) >> 11 & 1) != 0)
                            {
                                set.Add($"0x{a.LuidHigh:X8}_0x{a.LuidLow:X8}");
                            }
                            Marshal.FreeHGlobal(buf);
                            var c = new KmtClose { Handle = a.Handle };
                            D3DKMTCloseAdapter(ref c);
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(e.Adapters); }
            }
        }
        catch (Exception ex)
        {
            App.Log("adapter query failed: " + ex.Message);
        }
        return _npuLuids = set;
    }

    /// <summary>
    /// The way Task Manager reads it: every process's use of each engine, summed per engine; the
    /// busiest engine is the figure. The GPU's engines and the NPU's are separate sets.
    /// </summary>
    private static void SampleEngines(SysKind kind)
    {
        bool npu = kind == SysKind.Npu;
        var npus = NpuLuids();
        if (npu && npus.Count == 0)
        {
            Add(kind, 0, "No NPU on this PC");
            return;
        }
        if (_gpuMissing || !PerformanceCounterCategory.Exists("GPU Engine"))
        {
            _gpuMissing = true;
            Add(kind, 0, "No counters on this PC");
            return;
        }
        var data = new PerformanceCounterCategory("GPU Engine").ReadCategory();
        if (!data.Contains("Utilization Percentage")) return;
        var perEngine = new Dictionary<string, double>();
        bool first = _gpuPrev.Count == 0;
        var seen = new Dictionary<string, CounterSample>();
        foreach (DictionaryEntry de in data["Utilization Percentage"])
        {
            var id = (InstanceData)de.Value!;
            string name = id.InstanceName;
            seen[name] = id.Sample;
            if (!_gpuPrev.TryGetValue(name, out var prev)) continue;
            int at = name.IndexOf("_luid_", StringComparison.Ordinal);
            string engine = at >= 0 ? name[(at + 6)..] : name;   // "<luid>_phys_0_eng_3_engtype_3D"
            int phys = engine.IndexOf("_phys_", StringComparison.Ordinal);
            string luid = phys >= 0 ? engine[..phys] : engine;
            if (npus.Contains(luid) != npu) continue;
            perEngine[engine] = perEngine.GetValueOrDefault(engine) + CounterSample.Calculate(prev, id.Sample);
        }
        _gpuPrev.Clear();
        foreach (var kv in seen) _gpuPrev[kv.Key] = kv.Value;
        if (first) return;
        string detail = npu ? "Neural engine" : "Busiest engine";
        double top = 0;
        foreach (var kv in perEngine)
        {
            if (kv.Value <= top) continue;
            top = kv.Value;
            int t = kv.Key.IndexOf("engtype_", StringComparison.Ordinal);
            if (t >= 0) detail = kv.Key[(t + 8)..] + " engine";
        }
        Add(kind, Math.Clamp(top, 0, 100), detail);
    }

    // ---- network -----------------------------------------------------------------------------

    private static void SampleNetwork()
    {
        var data = new PerformanceCounterCategory("Network Interface").ReadCategory();
        if (!data.Contains("Bytes Total/sec")) return;
        bool first = _netPrev.Count == 0;
        var seen = new Dictionary<string, CounterSample>();
        double best = 0;
        string name = "";
        foreach (DictionaryEntry de in data["Bytes Total/sec"])
        {
            var id = (InstanceData)de.Value!;
            seen[id.InstanceName] = id.Sample;
            if (!_netPrev.TryGetValue(id.InstanceName, out var prev)) continue;
            double v = CounterSample.Calculate(prev, id.Sample);
            if (v > best)
            {
                best = v;
                name = id.InstanceName;
            }
        }
        _netPrev.Clear();
        foreach (var kv in seen) _netPrev[kv.Key] = kv.Value;
        if (first) return;
        Add(SysKind.Network, best * 8 / 1e6, name.Length > 0 ? "Busiest adapter: " + name : "Busiest adapter");
    }
}
