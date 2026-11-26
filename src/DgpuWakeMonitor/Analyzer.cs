using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace DgpuWakeMonitor;

public sealed record StackFrame(string Module, ulong Address, ulong Offset, string? Method, string? ModulePath)
{
    public bool IsKernel => Address >= 0xFFFF_8000_0000_0000;
    public string Display => Method is { Length: > 0 } ? $"{Module}!{Method}" : $"{Module}+0x{Offset:x}";
}

public sealed class WakeRecord
{
    public DateTime Time { get; init; }
    public int Pid { get; init; }
    public int Tid { get; init; }
    public string ProcessName { get; init; } = "";
    public string? ImagePath { get; init; }
    public string? CommandLine { get; init; }
    public int? Reason { get; set; }
    public RequestMatch ReasonMatch { get; init; }
    public DateTime? SleptAt { get; set; }
    public List<StackFrame> Stack { get; set; } = [];
    /// <summary>First user-mode module below the kernel thunks (e.g. nvml.dll, dxgi.dll, nvapi64.dll).</summary>
    public string? EntryModule { get; set; }
    /// <summary>First user-mode module outside Windows / the driver store, i.e. the app code that called in.</summary>
    public string? CallerModule { get; set; }
    public bool KernelInitiated => Pid is 0 or 4;
    public int PriorState { get; init; } = -1;
    public DateTime? AwakeAt { get; init; }
    public bool Completed => AwakeAt.HasValue;
    public bool IsConfirmedWake => Completed && PriorState == 3;
    /// <summary>The D0 IRP failed (device did not reach D0); not counted as a wake.</summary>
    public bool Failed { get; init; }
    public uint? FailStatus { get; init; }
}

public sealed record IgnoredInfo(DateTime Time, int Pid, string ProcessName, string Why);

public sealed class Analysis
{
    public required DateTime Start { get; init; }
    public required DateTime End { get; init; }
    /// <summary>All D0 requests that changed (or tried to change) the state, failed ones included.</summary>
    public required List<WakeRecord> Wakes { get; init; }
    /// <summary>D0 requests that were not wakes, e.g. made while the device was already in D0.</summary>
    public required List<IgnoredInfo> Ignored { get; init; }
    /// <summary>D-state at the start (0..3), or -1 if unknown.</summary>
    public int InitialState { get; init; } = -1;
    public DateTime? InitialD0End { get; init; }
    public DateTime? UncertainD0End { get; init; }
    public int PendingIrps { get; init; }
    public long TotalEvents { get; init; }
    /// <summary>Events ETW dropped, from the finalized trace.</summary>
    public int EventsLost { get; init; }
    /// <summary>Kernel-Power IRP completions for any device: zero means the provider data is missing.</summary>
    public int PowerIrpEvents { get; init; }
    /// <summary>SET_POWER IRPs for the dGPU, and how many of them lacked the PowerState field.</summary>
    public int DeviceIrps { get; init; }
    public int MissingStateIrps { get; init; }
    public int MissedWakes { get; init; }
    public TimeSpan Duration => End - Start;

    /// <summary>Reasons the wake list may be incomplete; empty when the trace is fully usable.</summary>
    public IEnumerable<string> Caveats
    {
        get
        {
            if (EventsLost > 0) yield return $"ETW dropped {EventsLost:N0} events while recording, so wakes may be missing.";
            if (PowerIrpEvents == 0) yield return "The trace contains no Kernel-Power IRP events at all, so power transitions could not be observed.";
            if (MissedWakes > 0) yield return $"ETW recorded {MissedWakes} successful D3 request(s) while the last known state was D3. There may have been an intervening wake.";
            if (PendingIrps > 0) yield return $"{PendingIrps} dGPU power request(s) had no matched completion. Their effect on the device state is unknown.";
            if (Wakes.Any(w => w.Completed && w.PriorState < 0)) yield return "At least one D0 request succeeded with an unknown prior state, so it could not be classified as a D3 → D0 wake.";
            if (MissingStateIrps > 0) yield return $"{MissingStateIrps} of {DeviceIrps} dGPU power requests have no PowerState field (Kernel-Power event 7 version 0, older Windows builds) and could not be classified.";
        }
    }
    public IEnumerable<WakeRecord> CountedWakes => Wakes.Where(w => w.IsConfirmedWake);

    public DateTime D0End(WakeRecord w)
    {
        var end = w.SleptAt ?? UncertainD0End ?? End;
        if (end > End) end = End;
        return w.AwakeAt is { } from && end < from ? from : end;
    }

    public TimeSpan AwakeDuration(WakeRecord w) => w.AwakeAt is { } from
        ? SpanWithinTrace(from, D0End(w)) : TimeSpan.Zero;

    TimeSpan SpanWithinTrace(DateTime from, DateTime to)
    {
        from = from < Start ? Start : from;
        to = to > End ? End : to;
        return to > from ? to - from : TimeSpan.Zero;
    }

    /// <summary>D0 intervals based on successful requests and the initial measurement.</summary>
    public IEnumerable<(DateTime From, DateTime To, WakeRecord? Wake)> D0Intervals
    {
        get
        {
            if (InitialState == 0)
            {
                var duration = SpanWithinTrace(Start, InitialD0End ?? UncertainD0End ?? End);
                if (duration > TimeSpan.Zero) yield return (Start, Start + duration, null);
            }
            foreach (var w in Wakes.Where(w => w.Completed).OrderBy(w => w.AwakeAt))
            {
                var duration = AwakeDuration(w);
                var from = w.AwakeAt!.Value < Start ? Start : w.AwakeAt.Value;
                if (duration > TimeSpan.Zero) yield return (from, from + duration, w);
            }
        }
    }

    public TimeSpan TimeInD0 => TimeSpan.FromTicks(D0Intervals.Sum(i => (i.To - i.From).Ticks));
}

static class Analyzer
{
    static readonly HashSet<string> ThunkModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "ntdll", "win32u", "gdi32", "gdi32full", "kernelbase", "kernel32", "user32", "ucrtbase", "msvcrt",
    };

    public static Analysis Analyze(string etlPath, string instanceId, int initialState, bool resolveSymbols, TextWriter log)
    {
        // Convert into a temp file this analysis owns. OpenOrConvert would reuse (and we'd then delete)
        // a sibling .etlx that might belong to the user, e.g. from PerfView.
        var etlxPath = Path.Combine(Path.GetTempPath(), $"dgpuWakeMonitor-{Guid.NewGuid():N}.etlx");
        TraceLog traceLog;
        try
        {
            TraceLog.CreateFromEventTraceLogFile(etlPath, etlxPath, new TraceLogOptions { ConversionLog = TextWriter.Null });
            traceLog = new TraceLog(etlxPath);
        }
        catch
        {
            try { File.Delete(etlxPath); } catch { }
            throw;
        }
        try
        {
            var tracker = new PowerTracker(initialState);
            long total = 0;
            int powerIrpEvents = 0;

            // TraceEvent objects are reused across iterations, so never keep one: the tagger copies what
            // we need (including call stacks, which are stable indexes into the log) while it is current.
            object? Tag(TraceEvent e, PowerEvtKind kind)
            {
                if (kind == PowerEvtKind.DxgRequest) return e.CallStack();
                var proc = e.Process();
                return new IrpInfo(e.CallStack(), NameFor(e.ProcessID, proc),
                    proc?.ImageFileName is { Length: > 0 } img ? img : null,
                    proc?.CommandLine is { Length: > 0 } c ? c : null);
            }

            foreach (var e in traceLog.Events)
            {
                total++;
                if (Etw.Parse(e, instanceId, Tag) is { } pe)
                {
                    if (pe.Kind == PowerEvtKind.IrpStop) powerIrpEvents++;
                    tracker.Process(pe);
                }
            }

            var wakes = new List<WakeRecord>();
            var stackSources = new Dictionary<WakeRecord, (TraceCallStack? irp, TraceCallStack? req)>();
            foreach (var t in tracker.Wakes.OrderBy(w => w.Time))
            {
                var info = t.IrpTag as IrpInfo;
                var match = tracker.MatchQuality(t);
                bool useRequest = match is RequestMatch.Verified or RequestMatch.Unverified;
                var w = new WakeRecord
                {
                    Time = t.Time, Pid = t.Pid, Tid = t.Tid,
                    ProcessName = info?.Name ?? NameFor(t.Pid, null),
                    ImagePath = info?.Image, CommandLine = info?.CommandLine,
                    Reason = useRequest ? t.Reason : null, ReasonMatch = match, SleptAt = t.SleptAt, PriorState = t.PriorState,
                    AwakeAt = t.AwakeAt, Failed = t.Failed, FailStatus = t.FailStatus,
                };
                stackSources[w] = (info?.Stack, useRequest ? t.RequestTag as TraceCallStack : null);
                wakes.Add(w);
            }
            var ignored = tracker.Ignored
                .Select(r => new IgnoredInfo(r.Time, r.Pid, (r.Tag as IrpInfo)?.Name ?? NameFor(r.Pid, null), r.Why))
                .ToList();

            if (resolveSymbols && wakes.Count > 0)
                ResolveSymbols(traceLog, stackSources.Values.SelectMany(s => new[] { s.irp, s.req }), log);

            foreach (var (w, src) in stackSources)
            {
                // Prefer whichever stack reaches further into user mode.
                var a = ToFrames(src.irp);
                var b = ToFrames(src.req);
                w.Stack = b.Count(f => !f.IsKernel) > a.Count(f => !f.IsKernel) ? b : a;
                Classify(w);
            }

            return new Analysis
            {
                Start = traceLog.SessionStartTime.ToUniversalTime(), End = traceLog.SessionEndTime.ToUniversalTime(), Wakes = wakes, Ignored = ignored,
                InitialState = tracker.InitialState, PendingIrps = tracker.PendingIrps, UncertainD0End = tracker.UncertainD0End,
                InitialD0End = tracker.InitialD0End, TotalEvents = total,
                EventsLost = traceLog.EventsLost, PowerIrpEvents = powerIrpEvents,
                DeviceIrps = tracker.DeviceIrps, MissingStateIrps = tracker.MissingStateIrps, MissedWakes = tracker.MissedWakes,
            };
        }
        finally
        {
            traceLog.Dispose();
            try { File.Delete(etlxPath); } catch { }
        }
    }

    sealed record IrpInfo(TraceCallStack? Stack, string Name, string? Image, string? CommandLine);

    static string NameFor(int pid, TraceProcess? p)
    {
        if (pid == 0) return "Idle";
        if (pid == 4) return "System";
        if (p?.ImageFileName is { Length: > 0 } img) return Path.GetFileName(img);
        return p?.Name is { Length: > 0 } n ? n + ".exe" : $"<pid {pid}>";
    }

    static List<StackFrame> ToFrames(TraceCallStack? cs)
    {
        var frames = new List<StackFrame>();
        for (var f = cs; f != null && frames.Count < 256; f = f.Caller)
        {
            var ca = f.CodeAddress;
            var mf = ca.ModuleFile;
            ulong baseAddr = mf?.ImageBase ?? 0;
            frames.Add(new StackFrame(
                Module: string.IsNullOrEmpty(ca.ModuleName) ? "?" : ca.ModuleName,
                Address: ca.Address,
                Offset: baseAddr != 0 ? ca.Address - baseAddr : ca.Address,
                Method: string.IsNullOrEmpty(ca.FullMethodName) ? null : ca.FullMethodName,
                ModulePath: mf?.FilePath));
        }
        return frames;
    }

    static void Classify(WakeRecord w)
    {
        var user = w.Stack.Where(f => !f.IsKernel).ToList();
        var entryIdx = user.FindIndex(f => !ThunkModules.Contains(f.Module));
        if (entryIdx < 0) return;
        w.EntryModule = user[entryIdx].Module;
        var below = user.Skip(entryIdx + 1).Where(f => !ThunkModules.Contains(f.Module)).ToList();
        // App code outside Windows; else the process's own executable (e.g. tools shipped in System32).
        var exe = Path.GetFileNameWithoutExtension(w.ProcessName);
        var caller = below.FirstOrDefault(f => !IsWindowsOrDriverModule(f))
                  ?? below.FirstOrDefault(f => string.Equals(f.Module, exe, StringComparison.OrdinalIgnoreCase));
        if (caller != null && caller.Module != w.EntryModule) w.CallerModule = caller.Module;
    }

    static bool IsWindowsOrDriverModule(StackFrame f)
    {
        if (f.ModulePath is not { Length: > 0 } p) return false;
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        // ETW paths may be in \Device\HarddiskVolumeN\Windows\... or \SystemRoot\... form.
        return p.StartsWith(win, StringComparison.OrdinalIgnoreCase)
            || p.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase);
    }

    static void ResolveSymbols(TraceLog traceLog, IEnumerable<TraceCallStack?> stacks, TextWriter log)
    {
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DgpuWakeMonitor", "symbols");
        var symPath = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH") is { Length: > 0 } env
            ? env
            : $"SRV*{cache}*https://msdl.microsoft.com/download/symbols;SRV*{cache}*https://driver-symbols.nvidia.com/";
        using var reader = new SymbolReader(TextWriter.Null, symPath) { SecurityCheck = _ => true };
        var modules = new HashSet<TraceModuleFile>();
        foreach (var s in stacks)
            for (var f = s; f != null; f = f.Caller)
                if (f.CodeAddress.ModuleFile is { } m) modules.Add(m);
        foreach (var m in modules)
        {
            log.WriteLine($"  symbols: {m.Name}");
            try { traceLog.CodeAddresses.LookupSymbolsForModule(reader, m); } catch (Exception ex) { log.WriteLine($"    failed: {ex.Message}"); }
        }
    }
}
