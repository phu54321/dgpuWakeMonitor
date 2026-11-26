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
    public DateTime? SleptAt { get; set; }
    public List<StackFrame> Stack { get; set; } = [];
    /// <summary>First user-mode module below the kernel thunks (e.g. nvml.dll, dxgi.dll, nvapi64.dll).</summary>
    public string? EntryModule { get; set; }
    /// <summary>First user-mode module outside Windows / the driver store, i.e. the app code that called in.</summary>
    public string? CallerModule { get; set; }
    public bool KernelInitiated => Pid is 0 or 4;
}

public sealed class Analysis
{
    public required DateTime Start { get; init; }
    public required DateTime End { get; init; }
    public required List<WakeRecord> Wakes { get; init; }
    public int? InitialState { get; init; }
    public long TotalEvents { get; init; }
    public TimeSpan Duration => End - Start;

    /// <summary>Time spent in D0 inside the recording window (open intervals run to the end).</summary>
    public TimeSpan TimeInD0
    {
        get
        {
            var t = TimeSpan.Zero;
            if (InitialState == 0)
            {
                var firstSleep = FirstSleepBeforeFirstWake;
                t += (firstSleep ?? (Wakes.Count > 0 ? Wakes[0].Time : End)) - Start;
            }
            foreach (var w in Wakes) t += (w.SleptAt ?? End) - w.Time;
            return t;
        }
    }
    public DateTime? FirstSleepBeforeFirstWake { get; init; }
}

static class Analyzer
{
    static readonly HashSet<string> ThunkModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "ntdll", "win32u", "gdi32", "gdi32full", "kernelbase", "kernel32", "user32", "ucrtbase", "msvcrt",
    };

    public static Analysis Analyze(string etlPath, string instanceId, bool resolveSymbols, TextWriter log)
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
            var wakes = new List<WakeRecord>();
            // TraceEvent objects are reused across iterations, so never keep one: take what we need
            // (including the call stack, which is a stable index into the log) while it is current.
            var lastRequest = new Dictionary<int, (DateTime time, int reason, TraceCallStack? stack)>();
            var stackSources = new Dictionary<WakeRecord, (TraceCallStack? irp, TraceCallStack? req)>();
            WakeRecord? open = null;
            int? initialState = null;
            DateTime? firstSleep = null;
            long total = 0;

            foreach (var e in traceLog.Events)
            {
                total++;
                if (Etw.IsPowerRequestDuringD3(e))
                {
                    lastRequest[e.ThreadID] = (e.TimeStamp, Convert.ToInt32(e.PayloadByName("Reason") ?? 0), e.CallStack());
                    continue;
                }
                int state = Etw.TryGetDevicePowerIrp(e, instanceId);
                if (state < 0) continue;

                if (state == 0)
                {
                    initialState ??= 3;
                    var proc = e.Process();
                    var w = new WakeRecord
                    {
                        Time = e.TimeStamp, Pid = e.ProcessID, Tid = e.ThreadID,
                        ProcessName = NameFor(e.ProcessID, proc),
                        ImagePath = proc?.ImageFileName is { Length: > 0 } ? proc.ImageFileName : null,
                        CommandLine = proc?.CommandLine is { Length: > 0 } c ? c : null,
                    };
                    TraceCallStack? reqStack = null;
                    if (lastRequest.TryGetValue(e.ThreadID, out var r) && (e.TimeStamp - r.time).TotalMilliseconds < 250)
                    {
                        w.Reason = r.reason;
                        reqStack = r.stack;
                    }
                    stackSources[w] = (e.CallStack(), reqStack);
                    wakes.Add(w);
                    open = w;
                }
                else if (state == 3)
                {
                    if (initialState == null) { initialState = 0; firstSleep = e.TimeStamp; }
                    if (open != null) { open.SleptAt = e.TimeStamp; open = null; }
                }
            }

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
                Start = traceLog.SessionStartTime, End = traceLog.SessionEndTime, Wakes = wakes,
                InitialState = initialState, FirstSleepBeforeFirstWake = firstSleep, TotalEvents = total,
            };
        }
        finally
        {
            traceLog.Dispose();
            try { File.Delete(etlxPath); } catch { }
        }
    }

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
