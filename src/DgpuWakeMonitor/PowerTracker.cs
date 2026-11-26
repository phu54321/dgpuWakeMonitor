namespace DgpuWakeMonitor;

public enum PowerEvtKind { DxgRequest, IrpStart, IrpStop }

/// <summary>
/// A power-relevant ETW event reduced to plain data. TraceEvent objects are reused by the parser,
/// so everything needed later (including stacks/process info in <see cref="Tag"/>) is copied out.
/// </summary>
public readonly record struct PowerEvt(
    PowerEvtKind Kind, DateTime Time, int Pid, int Tid,
    ulong Irp = 0, int State = -1, uint Status = 0, int Reason = 0, ulong Adapter = 0, object? Tag = null);

public sealed class TrackedWake
{
    public int Id { get; init; }
    public DateTime Time { get; init; }
    public int Pid { get; init; }
    public int Tid { get; init; }
    public ulong Irp { get; init; }
    public int PriorState { get; set; } = -1;
    public DateTime? AwakeAt { get; set; }
    public bool Completed => AwakeAt.HasValue;
    public bool IsConfirmedWake => Completed && PriorState == 3;
    public object? IrpTag { get; init; }
    public int? Reason { get; set; }
    public object? RequestTag { get; set; }
    /// <summary>AdapterFdoContext of the matched DxgKrnl request, if any.</summary>
    public ulong? RequestAdapter { get; set; }
    public DateTime? SleptAt { get; set; }
    /// <summary>The D0 IRP completed with a failure status: the device did not reach D0.</summary>
    public bool Failed { get; set; }
    public uint? FailStatus { get; set; }
}

/// <summary>How trustworthy the DxgKrnl PowerRequestDuringD3 match for a wake is.</summary>
public enum RequestMatch
{
    /// <summary>No preceding request on the same process/thread.</summary>
    None,
    /// <summary>Matched, but the request's adapter could not be confirmed as the dGPU's.</summary>
    Unverified,
    /// <summary>Matched, and the request's adapter is the one repeatedly seen before dGPU D0 requests.</summary>
    Verified,
    /// <summary>The only candidate was for a different adapter (e.g. the iGPU); not used.</summary>
    OtherAdapter,
}

public sealed record IgnoredRequest(DateTime Time, int Pid, int Tid, string Why, object? Tag);

/// <summary>
/// Tracks the dGPU device power state from Kernel-Power SET_POWER IRPs (event 7 start, event 8
/// completion). State and D0 intervals advance on successful completions. Request metadata
/// identifies the initiating thread. Events must be fed in timestamp order.
/// </summary>
public sealed class PowerTracker
{
    int _state;
    int _nextId;
    TrackedWake? _open;
    // DxgKrnl requests not yet consumed, per (pid, tid); pruned to the match window.
    readonly Dictionary<(int Pid, int Tid), List<PowerEvt>> _requests = new();
    // How often each adapter context was the one matched to a dGPU D0 request.
    readonly Dictionary<ulong, int> _adapterVotes = new();
    static readonly TimeSpan MatchWindow = TimeSpan.FromMilliseconds(250);
    readonly Dictionary<ulong, Pending> _pending = new();
    readonly List<TrackedWake> _finished = new();

    sealed record Pending(PowerEvt Request, TrackedWake? Wake);

    /// <param name="initialState">Measured D-state (0..3) when tracing started, or -1 if unknown.</param>
    public PowerTracker(int initialState)
    {
        _state = initialState;
        InitialState = initialState;
    }

    public int InitialState { get; }
    /// <summary>When the device first left D0, if it started the trace in D0.</summary>
    public DateTime? InitialD0End { get; private set; }
    public int State => _state;

    public IEnumerable<TrackedWake> Wakes => _finished.Concat(_pending.Values.Select(p => p.Wake).OfType<TrackedWake>());
    public List<IgnoredRequest> Ignored { get; } = new();
    public int CountedWakes { get; private set; }
    public int PendingIrps => _pending.Count;
    public DateTime? UncertainD0End => _state == 0
        ? _pending.Values.Where(p => p.Request.State != 0).Min(p => (DateTime?)p.Request.Time)
        : null;
    /// <summary>SET_POWER IRPs seen for the device.</summary>
    public int DeviceIrps { get; private set; }
    /// <summary>Device IRPs without a PowerState field (Kernel-Power event 7 v0): cannot be classified.</summary>
    public int MissingStateIrps { get; private set; }
    /// <summary>
    /// Successful D3 requests while already believed to be in D3. A wake may have gone unobserved.
    /// </summary>
    public int MissedWakes { get; private set; }
    public event Action<DateTime>? WakeMissed;

    public event Action<TrackedWake>? WakeResolved;
    public event Action<TrackedWake>? WakeEnded;
    public event Action<IgnoredRequest>? RequestIgnored;

    public void Process(in PowerEvt e)
    {
        switch (e.Kind)
        {
            case PowerEvtKind.DxgRequest: AddRequest(e); break;
            case PowerEvtKind.IrpStart: OnIrpStart(e); break;
            case PowerEvtKind.IrpStop: OnIrpStop(e); break;
        }
    }

    void OnIrpStart(in PowerEvt e)
    {
        DeviceIrps++;
        if (e.State < 0) MissingStateIrps++;
        TrackedWake? w = null;
        if (e.State == 0)
        {
            w = new TrackedWake
            {
                Id = ++_nextId, Time = e.Time, Pid = e.Pid, Tid = e.Tid, Irp = e.Irp,
                IrpTag = e.Tag,
            };
            MatchRequest(w, e);
        }
        _pending[e.Irp] = new Pending(e, w);
    }

    void OnIrpStop(in PowerEvt e)
    {
        if (!_pending.Remove(e.Irp, out var p)) return;
        if ((int)e.Status < 0)
        {
            if (p.Wake is { } failed)
            {
                failed.Failed = true;
                failed.FailStatus = e.Status;
                _finished.Add(failed);
                WakeResolved?.Invoke(failed);
            }
            return;
        }
        int previous = _state;
        _state = p.Request.State;
        if (p.Wake is { } w)
        {
            if (previous == 0)
            {
                var ignored = new IgnoredRequest(w.Time, w.Pid, w.Tid, "device already in D0", w.IrpTag);
                Ignored.Add(ignored);
                RequestIgnored?.Invoke(ignored);
                return;
            }
            w.PriorState = previous;
            w.AwakeAt = e.Time;
            _open = w;
            _finished.Add(w);
            if (w.IsConfirmedWake)
            {
                CountedWakes++;
                if (w.RequestAdapter is { } adapter)
                    _adapterVotes[adapter] = _adapterVotes.GetValueOrDefault(adapter) + 1;
            }
            WakeResolved?.Invoke(w);
        }
        else if (previous == 0)
        {
            if (_open is { } open)
            {
                open.SleptAt = e.Time;
                _open = null;
                WakeEnded?.Invoke(open);
            }
            else
                InitialD0End = e.Time;
        }
        else if (previous == 3 && _state == 3)
        {
            MissedWakes++;
            WakeMissed?.Invoke(e.Time);
        }
    }

    void AddRequest(in PowerEvt e)
    {
        if (!_requests.TryGetValue((e.Pid, e.Tid), out var list)) _requests[(e.Pid, e.Tid)] = list = new();
        var cutoff = e.Time - MatchWindow;
        list.RemoveAll(r => r.Time < cutoff);
        list.Add(e);
    }

    /// <summary>
    /// Attaches the DxgKrnl request that caused this D0 IRP: same process and thread, at or before the
    /// IRP and within <see cref="MatchWindow"/>, not used by an earlier IRP. Among candidates, prefer the
    /// adapter that earlier matches point to (event 368 also fires for the iGPU on the same thread).
    /// </summary>
    void MatchRequest(TrackedWake w, in PowerEvt irp)
    {
        if (!_requests.TryGetValue((irp.Pid, irp.Tid), out var list)) return;
        var irpTime = irp.Time;
        var candidates = list.Where(r => r.Time <= irpTime && irpTime - r.Time <= MatchWindow).ToList();
        if (candidates.Count == 0) return;
        var leader = LeadingAdapter;
        var pick = candidates.Where(r => leader != null && r.Adapter == leader).DefaultIfEmpty(candidates[^1]).Last();
        list.Remove(pick);
        w.Reason = pick.Reason;
        w.RequestTag = pick.Tag;
        w.RequestAdapter = pick.Adapter;
    }

    ulong? LeadingAdapter => _adapterVotes.Count == 0 ? null : _adapterVotes.MaxBy(kv => kv.Value).Key;

    /// <summary>The dGPU's adapter context, once one adapter has been matched at least twice.</summary>
    public ulong? DgpuAdapter => _adapterVotes.Count > 0 && _adapterVotes.Values.Max() >= 2 ? LeadingAdapter : null;

    /// <summary>Judges a wake's request match against everything seen so far (call after the trace).</summary>
    public RequestMatch MatchQuality(TrackedWake w)
    {
        if (w.RequestAdapter is not { } a) return RequestMatch.None;
        if (DgpuAdapter is not { } d) return RequestMatch.Unverified;
        return a == d ? RequestMatch.Verified : RequestMatch.OtherAdapter;
    }
}
