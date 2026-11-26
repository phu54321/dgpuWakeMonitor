using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace DgpuWakeMonitor;

/// <summary>
/// The ETW events used to detect and attribute dGPU wakes (verified against a live trace):
///  - Kernel-Power  id 7   "Irp/Start": IRP_MN_SET_POWER (MinorFunction=2) for a device.
///      PowerStateType=1 means device power; PowerState 0 = D0 (wake request), 3 = D3 (sleep).
///      InstanceName is the PnP instance ID, and the event fires on the requesting thread.
///  - Kernel-Power  id 8   "Irp/Stop": completion of that IRP (same Irp pointer) with an NTSTATUS.
///  - DxgKrnl       id 368 "PowerRequestDuringD3": a graphics call arrived while the adapter was in D3.
///      Same thread as the IRP; carries a Reason code and the adapter's FDO context.
///  - Kernel-Process id 1/2: process start/stop, used to name short-lived processes live.
/// </summary>
static class Etw
{
    public static readonly Guid DxgKrnl = new("802ec45a-1e99-4b83-9920-87c98277ba9d");
    public static readonly Guid KernelPower = new("331c3b3a-2005-44c2-ac5e-77220c37d6b4");
    public static readonly Guid KernelProcess = new("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716");

    public const ulong DxgKrnlDiagnostics = 0x400000;   // PowerRequestDuringD3
    public const ulong KernelPowerPerformance = 0x8;     // Irp start/stop
    public const ulong KernelProcessKeyword = 0x10;      // ProcessStart/Stop

    public const int IrpStartId = 7;
    public const int IrpStopId = 8;
    public const int PowerRequestDuringD3Id = 368;

    public const string LiveSessionName = "DgpuWakeMonitor-Live";
    public const string RecordSessionName = "DgpuWakeMonitor-Record";

    /// <summary>
    /// Reduces an event to a <see cref="PowerEvt"/> if it matters for wake tracking, else null.
    /// IRP starts are kept only for the given device; IRP stops are kept for all devices (the tracker
    /// matches them by Irp pointer). <paramref name="tagger"/> runs while <paramref name="e"/> is current.
    /// </summary>
    public static PowerEvt? Parse(TraceEvent e, string instanceId, Func<TraceEvent, PowerEvtKind, object?>? tagger = null)
    {
        if (e.ProviderGuid == DxgKrnl && (int)e.ID == PowerRequestDuringD3Id)
            return new PowerEvt(PowerEvtKind.DxgRequest, e.TimeStamp, e.ProcessID, e.ThreadID,
                Reason: Convert.ToInt32(e.PayloadByName("Reason") ?? 0),
                Adapter: Convert.ToUInt64(e.PayloadByName("AdapterFdoContext") ?? 0UL),
                Tag: tagger?.Invoke(e, PowerEvtKind.DxgRequest));

        if (e.ProviderGuid != KernelPower) return null;

        if ((int)e.ID == IrpStopId)
            return new PowerEvt(PowerEvtKind.IrpStop, e.TimeStamp, e.ProcessID, e.ThreadID,
                Irp: Convert.ToUInt64(e.PayloadByName("Irp") ?? 0UL),
                Status: Convert.ToUInt32(e.PayloadByName("Status") ?? 0u));

        if ((int)e.ID != IrpStartId) return null;
        if (Convert.ToInt32(e.PayloadByName("PowerStateType") ?? -1) != 1) return null;
        if (Convert.ToInt32(e.PayloadByName("MinorFunction") ?? -1) != 2) return null;
        if (!string.Equals(e.PayloadByName("InstanceName") as string, instanceId, StringComparison.OrdinalIgnoreCase)) return null;
        return new PowerEvt(PowerEvtKind.IrpStart, e.TimeStamp, e.ProcessID, e.ThreadID,
            Irp: Convert.ToUInt64(e.PayloadByName("Irp") ?? 0UL),
            State: Convert.ToInt32(e.PayloadByName("PowerState") ?? -1),
            Tag: tagger?.Invoke(e, PowerEvtKind.IrpStart));
    }

    /// <summary>Stops sessions left behind by a crashed or killed previous run.</summary>
    public static void StopStaleSessions()
    {
        foreach (var name in TraceEventSession.GetActiveSessionNames())
        {
            if (name != LiveSessionName && name != RecordSessionName) continue;
            try { using var s = new TraceEventSession(name, TraceEventSessionOptions.Attach); s.Stop(); } catch { }
        }
    }
}

/// <summary>Facts about a recording that the ETL itself doesn't carry; saved next to it.</summary>
public sealed record RecordingMeta(string InstanceId, string DeviceName, int InitialState, DateTime MeasuredAt)
{
    public static string PathFor(string etlPath) => Path.ChangeExtension(etlPath, ".meta.json");
    public void Save(string etlPath) => File.WriteAllText(PathFor(etlPath), JsonSerializer.Serialize(this));
    public static RecordingMeta? TryLoad(string etlPath)
    {
        try { return JsonSerializer.Deserialize<RecordingMeta>(File.ReadAllText(PathFor(etlPath))); }
        catch { return null; }
    }
}

/// <summary>A dGPU D3->D0 wake seen by the live session (no stacks; those come from the ETL).</summary>
public sealed record LiveWake(int Id, DateTime Time, int Pid, int Tid, string Process, int? Reason, bool PriorStateKnown);

/// <summary>
/// Real-time session: counts events and reports wakes/sleeps as they happen. Real-time ETW delivers
/// per-CPU buffers without a global order, so power events are held for <see cref="ReorderDelay"/>
/// and fed to the <see cref="PowerTracker"/> in timestamp order.
/// </summary>
sealed class LiveMonitor : IDisposable
{
    static readonly TimeSpan ReorderDelay = TimeSpan.FromSeconds(3);

    readonly TraceEventSession _session;
    readonly string _instanceId;
    readonly PowerTracker _tracker;
    readonly Dictionary<int, string> _names = new();
    readonly HashSet<int> _unnamed = new();
    readonly List<PowerEvt> _queue = new();
    readonly object _lock = new();
    readonly System.Threading.Timer _flushTimer;
    Thread? _thread;
    long _events;

    public event Action<LiveWake>? Wake;
    /// <summary>The wake with this id ended (device back to D3) after the given time awake.</summary>
    public event Action<int, DateTime, TimeSpan>? Sleep;
    /// <summary>The D0 request for this wake failed (status); it is no longer counted.</summary>
    public event Action<int, uint>? WakeFailed;
    /// <summary>A D0 request that did not count as a wake (time, process, why).</summary>
    public event Action<DateTime, string, string>? Ignored;
    /// <summary>A wake was reported before its process name was known; this delivers the name later.</summary>
    public event Action<int, string>? NameResolved;

    public long EventCount => Interlocked.Read(ref _events);
    public int WakeCount { get { lock (_lock) return _tracker.CountedWakes; } }

    /// <param name="initialState">dGPU D-state measured before the session started (-1 if unknown).</param>
    public LiveMonitor(string instanceId, int initialState)
    {
        _instanceId = instanceId;
        _tracker = new PowerTracker(initialState);
        _tracker.WakeStarted += w => Wake?.Invoke(new LiveWake(w.Id, w.Time, w.Pid, w.Tid, NameOf(w.Pid), w.Reason, w.PriorStateKnown));
        _tracker.WakeEnded += w => Sleep?.Invoke(w.Id, w.SleptAt!.Value, w.SleptAt.Value - w.Time);
        _tracker.WakeFailed += w => WakeFailed?.Invoke(w.Id, w.FailStatus ?? 0);
        _tracker.RequestIgnored += r => Ignored?.Invoke(r.Time, NameOf(r.Pid), r.Why);

        _session = new TraceEventSession(Etw.LiveSessionName) { StopOnDispose = true };
        _session.EnableProvider(Etw.KernelProcess, TraceEventLevel.Informational, Etw.KernelProcessKeyword);
        _session.EnableProvider(Etw.DxgKrnl, TraceEventLevel.Verbose, Etw.DxgKrnlDiagnostics);
        _session.EnableProvider(Etw.KernelPower, TraceEventLevel.Verbose, Etw.KernelPowerPerformance);
        _session.Source.Dynamic.All += OnEvent;
        _flushTimer = new System.Threading.Timer(_ => Flush(DateTime.Now - ReorderDelay), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        _thread = new Thread(() => { try { _session.Source.Process(); } catch { } }) { IsBackground = true, Name = "etw-live" };
        _thread.Start();
        _flushTimer.Change(500, 500);
    }

    void OnEvent(TraceEvent e)
    {
        Interlocked.Increment(ref _events);

        if (e.ProviderGuid == Etw.KernelProcess)
        {
            // Start events can arrive after the wake they explain; stop events carry the name too.
            if ((int)e.ID is 1 or 2 && e.PayloadByName("ImageName") is string img && img.Length > 0)
            {
                int pid = Convert.ToInt32(e.PayloadByName("ProcessID"));
                string name = Path.GetFileName(img);
                bool resolved;
                lock (_lock) { _names[pid] = name; resolved = _unnamed.Remove(pid); }
                if (resolved) NameResolved?.Invoke(pid, name);
            }
            return;
        }

        if (Etw.Parse(e, _instanceId) is { } pe)
            lock (_lock) _queue.Add(pe);
    }

    void Flush(DateTime horizon)
    {
        lock (_lock)
        {
            if (_queue.Count == 0) return;
            _queue.Sort((a, b) => a.Time.CompareTo(b.Time));
            int n = 0;
            while (n < _queue.Count && _queue[n].Time <= horizon) n++;
            for (int i = 0; i < n; i++) _tracker.Process(_queue[i]);
            _queue.RemoveRange(0, n);
        }
    }

    string NameOf(int pid)
    {
        if (pid == 0) return "Idle";
        if (pid == 4) return "System";
        lock (_lock)
        {
            if (_names.TryGetValue(pid, out var n)) return n;
            try { using var p = System.Diagnostics.Process.GetProcessById(pid); return _names[pid] = p.ProcessName + ".exe"; }
            catch { _unnamed.Add(pid); return $"<pid {pid}>"; }
        }
    }

    public void Dispose()
    {
        _flushTimer.Dispose();
        try { _session.Stop(); } catch { }
        _thread?.Join(2000);
        Flush(DateTime.MaxValue);
        _session.Dispose();
    }
}

/// <summary>
/// The in-app equivalent of a WPR recording: a file-mode ETL session with kernel process/image
/// events (for symbolizing user-mode stacks) and stacks on the two wake events only.
/// </summary>
sealed class Recorder : IDisposable
{
    readonly TraceEventSession _session;
    public string EtlPath { get; }
    public bool Stopped { get; private set; }

    public Recorder(string etlPath)
    {
        EtlPath = etlPath;
        _session = new TraceEventSession(Etw.RecordSessionName, etlPath) { StopOnDispose = true, BufferSizeMB = 64 };
        // Kernel provider must be enabled first in a session.
        _session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.ImageLoad);
        _session.EnableProvider(Etw.DxgKrnl, TraceEventLevel.Verbose, Etw.DxgKrnlDiagnostics,
            new TraceEventProviderOptions { EventIDStacksToEnable = [Etw.PowerRequestDuringD3Id] });
        _session.EnableProvider(Etw.KernelPower, TraceEventLevel.Verbose, Etw.KernelPowerPerformance,
            new TraceEventProviderOptions { EventIDStacksToEnable = [Etw.IrpStartId] });
    }

    public long FileSize { get { try { return new FileInfo(EtlPath).Length; } catch { return 0; } } }

    public void Stop()
    {
        if (Stopped) return;
        Stopped = true;
        try { _session.Stop(); } catch { }
    }

    public void Dispose() { Stop(); _session.Dispose(); }
}
