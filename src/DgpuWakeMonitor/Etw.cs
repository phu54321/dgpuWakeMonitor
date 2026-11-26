using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace DgpuWakeMonitor;

/// <summary>
/// The ETW events used to detect and attribute dGPU wakes (verified against a live trace):
///  - Kernel-Power  id 7   "Irp/Start": IRP_MN_SET_POWER (MinorFunction=2) for a device.
///      PowerStateType=1 means device power; PowerState 0 = D0 (wake request), 3 = D3 (sleep).
///      InstanceName is the PnP instance ID, and the event fires on the requesting thread.
///  - DxgKrnl       id 368 "PowerRequestDuringD3": a graphics call arrived while the adapter was in D3.
///      Same thread as the IRP; carries a Reason code.
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
    public const int PowerRequestDuringD3Id = 368;

    public const string LiveSessionName = "DgpuWakeMonitor-Live";
    public const string RecordSessionName = "DgpuWakeMonitor-Record";

    /// <summary>Returns the target D-state (0..3) if this is a device SET_POWER IRP for the given instance, else -1.</summary>
    public static int TryGetDevicePowerIrp(TraceEvent e, string instanceId)
    {
        if (e.ProviderGuid != KernelPower || (int)e.ID != IrpStartId) return -1;
        if (Convert.ToInt32(e.PayloadByName("PowerStateType") ?? -1) != 1) return -1;
        if (Convert.ToInt32(e.PayloadByName("MinorFunction") ?? -1) != 2) return -1;
        if (!string.Equals(e.PayloadByName("InstanceName") as string, instanceId, StringComparison.OrdinalIgnoreCase)) return -1;
        return Convert.ToInt32(e.PayloadByName("PowerState") ?? -1);
    }

    public static bool IsPowerRequestDuringD3(TraceEvent e) =>
        e.ProviderGuid == DxgKrnl && (int)e.ID == PowerRequestDuringD3Id;

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

/// <summary>A dGPU D3->D0 wake seen by the live session (no stacks; those come from the ETL).</summary>
public sealed record LiveWake(DateTime Time, int Pid, int Tid, string Process, int? Reason);

/// <summary>Real-time session: counts events and reports wakes/sleeps as they happen.</summary>
sealed class LiveMonitor : IDisposable
{
    readonly TraceEventSession _session;
    readonly string _instanceId;
    readonly Dictionary<int, string> _names = new();
    readonly Dictionary<int, (DateTime time, int reason)> _lastRequestByThread = new();
    Thread? _thread;
    long _events;
    DateTime? _wokeAt;

    public event Action<LiveWake>? Wake;
    public event Action<DateTime, TimeSpan?>? Sleep;
    /// <summary>A wake was reported before its process name was known; this delivers the name later.</summary>
    public event Action<int, string>? NameResolved;
    readonly HashSet<int> _unnamed = new();
    public long EventCount => Interlocked.Read(ref _events);
    public int WakeCount { get; private set; }

    public LiveMonitor(string instanceId)
    {
        _instanceId = instanceId;
        _session = new TraceEventSession(Etw.LiveSessionName) { StopOnDispose = true };
        _session.EnableProvider(Etw.KernelProcess, TraceEventLevel.Informational, Etw.KernelProcessKeyword);
        _session.EnableProvider(Etw.DxgKrnl, TraceEventLevel.Verbose, Etw.DxgKrnlDiagnostics);
        _session.EnableProvider(Etw.KernelPower, TraceEventLevel.Verbose, Etw.KernelPowerPerformance);
        _session.Source.Dynamic.All += OnEvent;
    }

    public void Start()
    {
        _thread = new Thread(() => { try { _session.Source.Process(); } catch { } }) { IsBackground = true, Name = "etw-live" };
        _thread.Start();
    }

    void OnEvent(TraceEvent e)
    {
        Interlocked.Increment(ref _events);

        if (e.ProviderGuid == Etw.KernelProcess)
        {
            // Real-time delivery is per-CPU buffer, not globally ordered: a process's start event can
            // arrive after its wake. Stop events also carry the image name, so learn names from both.
            if ((int)e.ID is 1 or 2 && e.PayloadByName("ImageName") is string img && img.Length > 0)
            {
                int pid = Convert.ToInt32(e.PayloadByName("ProcessID"));
                _names[pid] = Path.GetFileName(img);
                if (_unnamed.Remove(pid)) NameResolved?.Invoke(pid, _names[pid]);
            }
            return;
        }

        if (Etw.IsPowerRequestDuringD3(e))
        {
            _lastRequestByThread[e.ThreadID] = (e.TimeStamp, Convert.ToInt32(e.PayloadByName("Reason") ?? 0));
            return;
        }

        int state = Etw.TryGetDevicePowerIrp(e, _instanceId);
        if (state == 0)
        {
            int? reason = _lastRequestByThread.TryGetValue(e.ThreadID, out var r) && (e.TimeStamp - r.time).TotalMilliseconds < 250
                ? r.reason : null;
            _wokeAt = e.TimeStamp;
            WakeCount++;
            Wake?.Invoke(new LiveWake(e.TimeStamp, e.ProcessID, e.ThreadID, NameOf(e.ProcessID), reason));
        }
        else if (state == 3)
        {
            Sleep?.Invoke(e.TimeStamp, _wokeAt is { } w ? e.TimeStamp - w : null);
            _wokeAt = null;
        }
    }

    string NameOf(int pid)
    {
        if (pid == 0) return "Idle";
        if (pid == 4) return "System";
        if (_names.TryGetValue(pid, out var n)) return n;
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return _names[pid] = p.ProcessName + ".exe"; }
        catch { _unnamed.Add(pid); return $"<pid {pid}>"; }
    }

    public void Dispose()
    {
        try { _session.Stop(); } catch { }
        _thread?.Join(2000);
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

    public int EventsLost => _session.EventsLost;

    public long FileSize { get { try { return new FileInfo(EtlPath).Length; } catch { return 0; } } }

    public void Stop()
    {
        if (Stopped) return;
        Stopped = true;
        try { _session.Stop(); } catch { }
    }

    public void Dispose() { Stop(); _session.Dispose(); }
}
