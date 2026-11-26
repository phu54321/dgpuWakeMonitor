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
    /// <summary>False when the state before this request was not known (no measurement, no earlier IRP).</summary>
    public bool PriorStateKnown { get; init; }
    public object? IrpTag { get; init; }
    public int? Reason { get; set; }
    public object? RequestTag { get; set; }
    /// <summary>AdapterFdoContext of the matched DxgKrnl request, if any.</summary>
    public ulong? RequestAdapter { get; set; }
    public DateTime? SleptAt { get; set; }
    /// <summary>The D0 IRP completed successfully (event 8 seen with a success status).</summary>
    public bool Completed { get; set; }
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
/// completion) and turns them into wakes. Only a D0 request made while the device is not known to
/// be in D0 counts as a wake; a request whose IRP completes with a failure status is marked failed
/// and the state is rolled back. Events must be fed in timestamp order.
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

    sealed record Pending(int PrevState, TrackedWake? Wake, TrackedWake? Closed, bool ClosedInitial);

    /// <param name="initialState">Measured D-state (0..3) when tracing started, or -1 if unknown.</param>
    public PowerTracker(int initialState)
    {
        _state = initialState;
        InitialState = initialState;
        InitialStateMeasured = initialState >= 0;
    }

    /// <summary>State at the start of the trace: measured, or inferred from the first IRP (-1 if neither).</summary>
    public int InitialState { get; private set; }
    public bool InitialStateMeasured { get; }
    /// <summary>When the device first left D0, if it started the trace in D0.</summary>
    public DateTime? InitialD0End { get; private set; }
    public int State => _state;

    public List<TrackedWake> Wakes { get; } = new();
    public List<IgnoredRequest> Ignored { get; } = new();
    public int CountedWakes => Wakes.Count(w => !w.Failed);

    public event Action<TrackedWake>? WakeStarted;
    public event Action<TrackedWake>? WakeEnded;
    public event Action<TrackedWake>? WakeFailed;
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
        if (e.State < 0) return;
        int prev = _state;
        if (InitialState < 0) InitialState = e.State == 0 ? 3 : 0; // best guess: a request implies a change

        if (e.State == 0)
        {
            if (prev == 0)
            {
                var ig = new IgnoredRequest(e.Time, e.Pid, e.Tid, "device already in D0", e.Tag);
                Ignored.Add(ig);
                _pending[e.Irp] = new Pending(prev, null, null, false);
                RequestIgnored?.Invoke(ig);
                return;
            }
            var w = new TrackedWake
            {
                Id = ++_nextId, Time = e.Time, Pid = e.Pid, Tid = e.Tid, Irp = e.Irp,
                PriorStateKnown = prev > 0, IrpTag = e.Tag,
            };
            MatchRequest(w, e);
            Wakes.Add(w);
            _open = w;
            _state = 0;
            _pending[e.Irp] = new Pending(prev, w, null, false);
            WakeStarted?.Invoke(w);
        }
        else
        {
            if (prev > 0) { _pending[e.Irp] = new Pending(prev, null, null, false); _state = e.State; return; }
            TrackedWake? closed = null;
            bool closedInitial = false;
            if (_open != null)
            {
                closed = _open;
                _open.SleptAt = e.Time;
                _open = null;
                WakeEnded?.Invoke(closed);
            }
            else if (InitialState == 0 && InitialD0End == null && Wakes.Count == 0)
            {
                InitialD0End = e.Time;
                closedInitial = true;
            }
            _state = e.State;
            _pending[e.Irp] = new Pending(prev, null, closed, closedInitial);
        }
    }

    void OnIrpStop(in PowerEvt e)
    {
        if (!_pending.Remove(e.Irp, out var p)) return;
        if ((int)e.Status >= 0) // NT_SUCCESS
        {
            if (p.Wake != null) p.Wake.Completed = true;
            return;
        }
        // The transition did not happen: roll the state back.
        _state = p.PrevState;
        if (p.Wake != null)
        {
            p.Wake.Failed = true;
            p.Wake.FailStatus = e.Status;
            if (_open == p.Wake) _open = null;
            WakeFailed?.Invoke(p.Wake);
        }
        if (p.Closed != null) { p.Closed.SleptAt = null; _open = p.Closed; }
        if (p.ClosedInitial) InitialD0End = null;
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
        _adapterVotes[pick.Adapter] = _adapterVotes.GetValueOrDefault(pick.Adapter) + 1;
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
