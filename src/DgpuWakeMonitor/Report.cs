using System.Globalization;
using System.Net;
using System.Text;

namespace DgpuWakeMonitor;

public sealed record ReportContext(GpuDevice Device, string EtlPath, int EventsLost, int? StateAtEnd, bool SymbolsResolved);

static class Report
{
    static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
    static string Dur(TimeSpan t) => t.TotalSeconds < 60 ? $"{t.TotalSeconds:0.00} s"
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{(int)t.TotalHours} h {t.Minutes} min";
    static string Ts(DateTime t) => t.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
    static string State(int? s) => s switch { 0 => "D0 (awake)", 3 => "D3 (asleep)", null or < 0 => "unknown", _ => $"D{s}" };

    public static string Build(Analysis a, ReportContext ctx)
    {
        var sb = new StringBuilder();
        double total = Math.Max(a.Duration.TotalMilliseconds, 1);
        double pct = a.TimeInD0.TotalMilliseconds / total * 100;

        sb.Append("""
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>dGPU Wake Report</title>
<style>
:root{--bg:#f6f7f9;--card:#fff;--ink:#1d2330;--muted:#5d6675;--line:#e2e5ea;--accent:#2f6fde;
--bad:#c8382f;--bad-bg:#fdeeed;--good:#1f8a4c;--good-bg:#e9f6ef;--d0:#e0553d;--d3:#dfe3e8;--mono:ui-monospace,"Cascadia Mono",Consolas,monospace}
@media (prefers-color-scheme:dark){:root{--bg:#121418;--card:#1b1e24;--ink:#e6e8ec;--muted:#9aa3b2;--line:#2c3038;--accent:#6ea0ff;
--bad:#ff7a70;--bad-bg:#3a1f1e;--good:#5fd394;--good-bg:#173323;--d0:#ff6b50;--d3:#2f343d;color-scheme:dark}}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--ink);font:14px/1.5 "Segoe UI",system-ui,sans-serif}
main{max-width:1040px;margin:0 auto;padding:32px 20px 64px}
h1{font-size:26px;margin:0 0 4px;font-weight:650}
h2{font-size:17px;margin:32px 0 10px;font-weight:650}
.sub{color:var(--muted);margin:0 0 20px}
.card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:16px 18px;margin-bottom:14px}
table{border-collapse:collapse;width:100%}
td,th{text-align:left;padding:6px 10px;border-bottom:1px solid var(--line);vertical-align:top}
th{color:var(--muted);font-weight:600;font-size:12px;text-transform:uppercase;letter-spacing:.04em}
tr:last-child td{border-bottom:none}
.kv td:first-child{color:var(--muted);width:200px;white-space:nowrap}
.num{text-align:right;font-variant-numeric:tabular-nums}
.mono{font-family:var(--mono);font-size:12.5px;word-break:break-all}
.verdict{border-radius:10px;padding:16px 18px;margin-bottom:14px;border:1px solid}
.verdict.bad{background:var(--bad-bg);border-color:var(--bad);color:var(--ink)}
.verdict.good{background:var(--good-bg);border-color:var(--good)}
.verdict b{font-size:18px}
.stats{display:flex;flex-wrap:wrap;gap:12px;margin-top:12px}
.stat{flex:1 1 150px;background:var(--card);border:1px solid var(--line);border-radius:8px;padding:10px 12px}
.stat .v{font-size:20px;font-weight:650;font-variant-numeric:tabular-nums}
.stat .l{color:var(--muted);font-size:12px}
.timeline{position:relative;height:28px;background:var(--d3);border-radius:6px;overflow:hidden}
.timeline span{position:absolute;top:0;bottom:0;background:var(--d0);min-width:2px}
.axis{display:flex;justify-content:space-between;color:var(--muted);font-size:12px;margin-top:4px;font-variant-numeric:tabular-nums}
.legend{color:var(--muted);font-size:12px;margin-top:6px}
.legend i{display:inline-block;width:10px;height:10px;border-radius:2px;margin:0 4px 0 10px;vertical-align:-1px}
.wake{border-left:4px solid var(--bad)}
.wake.sys{border-left-color:var(--accent)}
.wake h3{margin:0 0 8px;font-size:15px}
.tag{display:inline-block;font-size:11.5px;padding:1px 7px;border-radius:999px;border:1px solid var(--line);color:var(--muted);margin-left:6px;font-weight:500}
details{margin-top:10px}
summary{cursor:pointer;color:var(--accent);font-weight:600}
ol.stack{margin:8px 0 0;padding-left:44px;font-family:var(--mono);font-size:12.5px}
ol.stack li.k{color:var(--muted)}
ol.stack li.hot{font-weight:700;color:var(--bad)}
.scroll{overflow-x:auto}
footer{color:var(--muted);font-size:12px;margin-top:36px}
code{font-family:var(--mono);font-size:12.5px;background:var(--bg);padding:1px 5px;border-radius:4px}
</style></head><body><main>
<h1>dGPU Wake Report</h1>
""");
        sb.Append($"<p class=\"sub\">Which processes brought the discrete GPU out of D3 (sleep) into D0 (awake). Generated {H(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))}.</p>");

        var counted = a.CountedWakes.ToList();

        // Verdict
        if (counted.Count == 0)
            sb.Append($"""<div class="verdict good"><b>No dGPU wakes</b><div>The discrete GPU was not woken during the {Dur(a.Duration)} recording.</div></div>""");
        else
        {
            var top = counted.GroupBy(w => w.ProcessName).OrderByDescending(g => g.Count()).First();
            var plural = counted.Count == 1 ? "" : "s";
            sb.Append($"<div class=\"verdict bad\"><b>{counted.Count} dGPU wake{plural}</b><div>Most frequent culprit: <b>{H(top.Key)}</b> ({top.Count()}×). The dGPU was awake {pct:0.0}% of the recording.</div></div>");
        }

        sb.Append("<div class=\"stats\">");
        Stat(sb, counted.Count.ToString(), "wakes (D3 → D0)");
        Stat(sb, Dur(a.TimeInD0), "time in D0");
        Stat(sb, $"{pct:0.0}%", "D0 residency");
        Stat(sb, Dur(a.Duration), "recording length");
        sb.Append("</div>");

        // Timeline
        sb.Append("<h2>Power state timeline</h2><div class=\"card\"><div class=\"timeline\">");
        void Seg(DateTime from, DateTime to, string title)
        {
            double l = (from - a.Start).TotalMilliseconds / total * 100, wd = (to - from).TotalMilliseconds / total * 100;
            sb.Append(CultureInfo.InvariantCulture, $"<span style=\"left:{l:0.###}%;width:{wd:0.###}%\" title=\"{H(title)}\"></span>");
        }
        foreach (var (from, to, w) in a.D0Intervals)
            Seg(from, to, w == null ? "D0 at start of recording" : $"{w.ProcessName} · {Ts(w.Time)} · {Dur(to - from)}");
        sb.Append($"</div><div class=\"axis\"><span>{Ts(a.Start)}</span><span>{Ts(a.Start + a.Duration / 2)}</span><span>{Ts(a.End)}</span></div>");
        sb.Append("<div class=\"legend\"><i style=\"background:var(--d0)\"></i>D0 awake<i style=\"background:var(--d3)\"></i>D3 asleep / idle</div></div>");

        // Culprits
        if (counted.Count > 0)
        {
            sb.Append("<h2>Culprits</h2><div class=\"card scroll\"><table><tr><th>Process</th><th class=\"num\">Wakes</th><th class=\"num\">Time in D0</th><th>Entry module</th><th>Caller module</th></tr>");
            foreach (var g in counted.GroupBy(w => w.ProcessName).OrderByDescending(g => g.Count()).ThenBy(g => g.Key))
            {
                var d0 = TimeSpan.FromTicks(g.Sum(w => ((w.SleptAt ?? a.End) - w.Time).Ticks));
                string mods(Func<WakeRecord, string?> f) => H(string.Join(", ", g.Select(f).Where(m => m != null).Distinct()));
                sb.Append($"<tr><td><b>{H(g.Key)}</b></td><td class=\"num\">{g.Count()}</td><td class=\"num\">{Dur(d0)}</td><td class=\"mono\">{mods(w => w.EntryModule)}</td><td class=\"mono\">{mods(w => w.CallerModule)}</td></tr>");
            }
            sb.Append("</table></div>");
        }

        // Wake details
        if (a.Wakes.Count > 0) sb.Append("<h2>Wake events</h2>");
        int i = 0;
        foreach (var w in a.Wakes)
        {
            i++;
            sb.Append($"<div class=\"card wake{(w.KernelInitiated || w.Failed ? " sys" : "")}\"><h3>#{i} · {Ts(w.Time)} · {H(w.ProcessName)}");
            sb.Append($"<span class=\"tag\">+{Dur(w.Time - a.Start)}</span>");
            if (w.Failed) sb.Append($"<span class=\"tag\">request failed · not counted</span>");
            if (!w.PriorStateKnown) sb.Append("<span class=\"tag\">prior state unknown</span>");
            if (w.KernelInitiated) sb.Append("<span class=\"tag\">kernel-initiated</span>");
            sb.Append("</h3><table class=\"kv\">");
            Row(sb, "Process", $"{H(w.ProcessName)} (PID {w.Pid}, TID {w.Tid})");
            if (w.ImagePath != null) Row(sb, "Image", $"<span class=\"mono\">{H(w.ImagePath)}</span>");
            if (w.CommandLine != null) Row(sb, "Command line", $"<span class=\"mono\">{H(w.CommandLine)}</span>");
            if (w.Failed) Row(sb, "D0 request", $"failed with status <span class=\"mono\">0x{w.FailStatus:X8}</span>; the device stayed in its low-power state");
            else Row(sb, "Awake for", w.SleptAt is { } s ? $"{Dur(s - w.Time)} (back to D3 at {Ts(s)})" : "still in D0 when recording stopped");
            if (!w.Failed && !w.Completed) Row(sb, "Completion", "<span style=\"color:var(--muted)\">IRP completion not seen in the trace</span>");
            Row(sb, "Entry module", w.EntryModule != null ? $"<span class=\"mono\">{H(w.EntryModule)}</span>" : "<span style=\"color:var(--muted)\">no user-mode frames</span>");
            if (w.CallerModule != null) Row(sb, "Caller module", $"<span class=\"mono\">{H(w.CallerModule)}</span>");
            Row(sb, "DxgKrnl reason", w.ReasonMatch switch
            {
                RequestMatch.Verified => $"{w.Reason}",
                RequestMatch.Unverified => $"{w.Reason} <span class=\"tag\">hint · adapter not verified</span>",
                RequestMatch.OtherAdapter => "<span style=\"color:var(--muted)\">only a request for another adapter on this thread; not used</span>",
                _ => "<span style=\"color:var(--muted)\">no preceding PowerRequestDuringD3 on this thread</span>",
            });
            sb.Append("</table>");

            var user = w.Stack.Where(f => !f.IsKernel).ToList();
            if (user.Count > 0)
            {
                sb.Append($"<details open><summary>User-mode stack ({user.Count} frames)</summary><div class=\"scroll\"><ol class=\"stack\">");
                foreach (var f in user)
                    sb.Append($"<li{(f.Module == w.EntryModule || f.Module == w.CallerModule ? " class=\"hot\"" : "")}>{H(f.Display)}</li>");
                sb.Append("</ol></div></details>");
            }
            if (w.Stack.Count > 0)
            {
                sb.Append($"<details><summary>Full stack incl. kernel ({w.Stack.Count} frames)</summary><div class=\"scroll\"><ol class=\"stack\">");
                foreach (var f in w.Stack) sb.Append($"<li{(f.IsKernel ? " class=\"k\"" : "")}>{H(f.Display)}</li>");
                sb.Append("</ol></div></details>");
            }
            sb.Append("</div>");
        }

        if (a.Ignored.Count > 0)
        {
            sb.Append($"<h2>D0 requests not counted ({a.Ignored.Count})</h2><div class=\"card scroll\"><table><tr><th>Time</th><th>Process</th><th class=\"num\">PID</th><th>Why</th></tr>");
            foreach (var r in a.Ignored)
                sb.Append($"<tr><td>{Ts(r.Time)}</td><td>{H(r.ProcessName)}</td><td class=\"num\">{r.Pid}</td><td>{H(r.Why)}</td></tr>");
            sb.Append("</table></div>");
        }

        // System info
        sb.Append("<h2>Recording</h2><div class=\"card scroll\"><table class=\"kv\">");
        Row(sb, "Computer", H(Environment.MachineName));
        Row(sb, "OS", H($"{Environment.OSVersion.VersionString}"));
        Row(sb, "Device", H(ctx.Device.Name));
        Row(sb, "Instance ID", $"<span class=\"mono\">{H(ctx.Device.InstanceId)}</span>");
        Row(sb, "Started", H(a.Start.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")));
        Row(sb, "Duration", Dur(a.Duration));
        Row(sb, "State at start", State(a.InitialState) + (a.InitialState < 0 ? "" : a.InitialStateMeasured ? " · measured" : " · inferred from the first request"));
        Row(sb, "State at end", State(ctx.StateAtEnd));
        Row(sb, "Events in trace", a.TotalEvents.ToString("N0"));
        Row(sb, "Events lost", ctx.EventsLost == 0 ? "0" : $"<b style=\"color:var(--bad)\">{ctx.EventsLost:N0}</b>");
        Row(sb, "Symbols", ctx.SymbolsResolved ? "resolved" : "module+offset only (tick <i>Resolve symbols</i> before stopping to resolve Windows frames)");
        Row(sb, "Trace file", File.Exists(ctx.EtlPath) ? $"<span class=\"mono\">{H(ctx.EtlPath)}</span>" : "deleted");
        sb.Append("</table></div>");

        sb.Append("""
<footer>A wake is a Kernel-Power <code>IRP_MN_SET_POWER</code> D0 request for the device made while it was not in D0, attributed to the
requesting thread. Requests whose IRP completed with a failure status are listed but not counted. The reason code comes from a matching
DxgKrnl <code>PowerRequestDuringD3</code> event. Open the trace file in Windows Performance Analyzer for deeper analysis.</footer>
</main></body></html>
""");
        return sb.ToString();
    }

    static void Stat(StringBuilder sb, string v, string l) => sb.Append($"<div class=\"stat\"><div class=\"v\">{H(v)}</div><div class=\"l\">{H(l)}</div></div>");
    static void Row(StringBuilder sb, string k, string vHtml) => sb.Append($"<tr><td>{H(k)}</td><td>{vHtml}</td></tr>");
}
