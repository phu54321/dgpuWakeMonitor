// Copyright (c) 2026 Park Hyunwoo
// SPDX-License-Identifier: MIT

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
            return new PowerEvt(PowerEvtKind.DxgRequest, e.TimeStamp.ToUniversalTime(), e.ProcessID, e.ThreadID,
                Reason: Convert.ToInt32(e.PayloadByName("Reason") ?? 0),
                Adapter: Convert.ToUInt64(e.PayloadByName("AdapterFdoContext") ?? 0UL),
                Tag: tagger?.Invoke(e, PowerEvtKind.DxgRequest));

        if (e.ProviderGuid != KernelPower) return null;

        if ((int)e.ID == IrpStopId && e.PayloadByName("Status") is { } status)
            return new PowerEvt(PowerEvtKind.IrpStop, e.TimeStamp.ToUniversalTime(), e.ProcessID, e.ThreadID,
                Irp: Convert.ToUInt64(e.PayloadByName("Irp") ?? 0UL),
                Status: unchecked((uint)Convert.ToInt64(status)));

        if ((int)e.ID != IrpStartId) return null;
        if (Convert.ToInt32(e.PayloadByName("PowerStateType") ?? -1) != 1) return null;
        if (Convert.ToInt32(e.PayloadByName("MinorFunction") ?? -1) != 2) return null;
        if (!string.Equals(e.PayloadByName("InstanceName") as string, instanceId, StringComparison.OrdinalIgnoreCase)) return null;
        return new PowerEvt(PowerEvtKind.IrpStart, e.TimeStamp.ToUniversalTime(), e.ProcessID, e.ThreadID,
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
public sealed record LiveWake(int Id, DateTime Time, int Pid, int Tid, string Process, int? Reason, int PriorState, uint? FailStatus);

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
    // Process lifetimes per pid, from Kernel-Process start/stop events whenever they arrive: they can
    // arrive several seconds late and pids are reused quickly, so a name is looked up by the wake's time.
    sealed record Life(string Name, DateTime? Stop);
    readonly Dictionary<int, SortedList<DateTime, Life>> _lives = new();
    // Recent wakes and the name shown for each; re-resolved when lifetimes change, so a guess made
    // before the relevant process events arrived corrects itself.
    sealed class Shown { public int Id; public int Pid; public DateTime Time; public string Name = ""; public string? Guess; }
    readonly List<Shown> _shown = new();
    readonly List<PowerEvt> _queue = new();
    readonly object _lock = new();
    readonly System.Threading.Timer _flushTimer;
    Thread? _thread;
    long _events;

    public event Action<LiveWake>? Wake;
    /// <summary>The confirmed D0 interval with this id ended.</summary>
    public event Action<int, DateTime, TimeSpan>? Sleep;
    /// <summary>A repeated D3 completion may indicate an uncaptured wake.</summary>
    public event Action<DateTime>? WakeMissed;
    /// <summary>A D0 request that did not count as a wake (time, process, why).</summary>
    public event Action<DateTime, string, string>? Ignored;
    /// <summary>The process name shown for these wake ids was wrong or a guess; this is the better name.</summary>
    public event Action<IReadOnlyList<int>, string>? NameResolved;

    public long EventCount => Interlocked.Read(ref _events);
    public int WakeCount { get { lock (_lock) return _tracker.CountedWakes; } }

    /// <param name="initialState">dGPU D-state measured before the session started (-1 if unknown).</param>
    public LiveMonitor(string instanceId, int initialState)
    {
        _instanceId = instanceId;
        _tracker = new PowerTracker(initialState);
        _tracker.WakeResolved += w => Wake?.Invoke(new LiveWake(w.Id, w.Time, w.Pid, w.Tid, Track(w.Id, w.Pid, w.Time), w.Reason, w.PriorState, w.FailStatus));
        _tracker.WakeEnded += w => Sleep?.Invoke(w.Id, w.SleptAt!.Value, w.SleptAt.Value - w.AwakeAt!.Value);
        _tracker.WakeMissed += t => WakeMissed?.Invoke(t);
        _tracker.RequestIgnored += r => Ignored?.Invoke(r.Time, NameAt(r.Pid, r.Time, Guess(r.Pid, r.Time)), r.Why);

        _session = new TraceEventSession(Etw.LiveSessionName) { StopOnDispose = true };
        _session.EnableProvider(Etw.KernelProcess, TraceEventLevel.Informational, Etw.KernelProcessKeyword);
        _session.EnableProvider(Etw.DxgKrnl, TraceEventLevel.Verbose, Etw.DxgKrnlDiagnostics);
        _session.EnableProvider(Etw.KernelPower, TraceEventLevel.Verbose, Etw.KernelPowerPerformance);
        _session.Source.Dynamic.All += OnEvent;
        _flushTimer = new System.Threading.Timer(_ => Flush(DateTime.UtcNow - ReorderDelay), null, Timeout.Infinite, Timeout.Infinite);
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
            if ((int)e.ID is 1 or 2 && e.PayloadByName("ImageName") is string img && img.Length > 0
                && FileTime(e.PayloadByName("CreateTime")) is { } created)
            {
                int pid = Convert.ToInt32(e.PayloadByName("ProcessID"));
                DateTime? stopped = (int)e.ID == 2 ? FileTime(e.PayloadByName("ExitTime")) ?? e.TimeStamp.ToUniversalTime() : null;
                List<(int, string)> changes;
                lock (_lock)
                {
                    RecordLife(pid, Path.GetFileName(img), created, stopped);
                    changes = Reresolve(pid);
                }
                foreach (var (id, name) in changes) NameResolved?.Invoke([id], name);
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

    static DateTime? FileTime(object? value) =>
        value is DateTime date && date.ToUniversalTime() > DateTime.FromFileTimeUtc(0)
            ? date.ToUniversalTime() : null;

    void RecordLife(int pid, string name, DateTime created, DateTime? stopped)
    {
        if (!_lives.TryGetValue(pid, out var list)) _lives[pid] = list = new();
        // Both event types identify the same process by creation time. A late start can
        // improve the name without discarding a stop that arrived first.
        list.TryGetValue(created, out var life);
        list[created] = new Life(stopped == null ? name : life?.Name ?? name, stopped ?? life?.Stop);
    }

    /// <summary>Name of the process that had this pid at time t, if the lifetimes seen so far say so.</summary>
    string? Resolve(int pid, DateTime t)
    {
        if (!_lives.TryGetValue(pid, out var list)) return null;
        t = t.ToUniversalTime();
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list.Keys[i] > t) continue;
            var life = list.Values[i];
            return life.Stop == null || t <= life.Stop ? life.Name : null;
        }
        return null;
    }

    string NameAt(int pid, DateTime t, string? guess) => pid switch
    {
        0 => "Idle",
        4 => "System",
        _ => Resolve(pid, t) ?? (guess != null ? guess + "?" : $"<pid {pid}>"),
    };

    static string? Guess(int pid, DateTime time)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return p.StartTime.ToUniversalTime() <= time.ToUniversalTime() ? p.ProcessName + ".exe" : null;
        }
        catch { return null; }
    }

    string Track(int id, int pid, DateTime t)
    {
        lock (_lock)
        {
            var s = new Shown { Id = id, Pid = pid, Time = t, Guess = Guess(pid, t) };
            s.Name = NameAt(pid, t, s.Guess);
            _shown.RemoveAll(x => x.Time < t - TimeSpan.FromMinutes(10));
            _shown.Add(s);
            return s.Name;
        }
    }

    List<(int Id, string Name)> Reresolve(int pid)
    {
        var changes = new List<(int, string)>();
        foreach (var s in _shown)
        {
            if (s.Pid != pid) continue;
            var n = NameAt(s.Pid, s.Time, s.Guess);
            if (n != s.Name) { s.Name = n; changes.Add((s.Id, n)); }
        }
        return changes;
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
