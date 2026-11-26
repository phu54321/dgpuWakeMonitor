using System.Diagnostics;

namespace DgpuWakeMonitor;

sealed class MainForm : Form
{
    static readonly string DataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DgpuWakeMonitor", "recordings");

    readonly GpuDevice? _device;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };

    // UI
    readonly Label _deviceLabel = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 11f) };
    readonly Label _statePill = new() { AutoSize = true, Padding = new Padding(8, 3, 8, 3), Font = new Font("Segoe UI Semibold", 9.5f), Margin = new Padding(10, 2, 0, 0) };
    readonly Label _status = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 6, 3, 3) };
    readonly Label _events = StatValue(), _elapsed = StatValue(), _wakes = StatValue(), _size = StatValue();
    readonly Button _start = new() { Text = "Start recording", AutoSize = true };
    readonly Button _stop = new() { Text = "Stop && build report", AutoSize = true, Enabled = false };
    readonly Button _openReport = new() { Text = "Open report", AutoSize = true, Enabled = false };
    readonly Button _openFolder = new() { Text = "Open folder", AutoSize = true };
    readonly CheckBox _symbols = new() { Text = "Resolve symbols (slow, downloads PDBs)", AutoSize = true, Margin = new Padding(12, 7, 3, 3) };
    readonly ListView _list = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, HideSelection = false };
    readonly Button _logToggle = new() { Text = "▾ Log", AutoSize = true, FlatStyle = FlatStyle.Flat };
    readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill,
        Font = new Font("Cascadia Mono", 9f), BackColor = Color.FromArgb(24, 26, 30), ForeColor = Color.FromArgb(214, 218, 224),
    };
    readonly TableLayoutPanel _root = new() { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12) };

    // Recording state
    LiveMonitor? _live;
    Recorder? _recorder;
    string? _runDir;
    string? _reportPath;
    DateTime _startedAt;
    int _initialState = -1;
    readonly Dictionary<int, ListViewItem> _liveRows = new();
    bool _busy;

    public MainForm()
    {
        // Fonts (in points) follow the monitor DPI and AutoSize controls size from them;
        // only the few fixed pixel sizes are scaled by hand.
        AutoScaleMode = AutoScaleMode.None;
        Text = "dgpuWakeMonitor";
        using (var ico = typeof(MainForm).Assembly.GetManifestResourceStream("DgpuWakeMonitor.icon.ico"))
            if (ico != null) Icon = new Icon(ico);
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(LogicalToDeviceUnits(980), LogicalToDeviceUnits(680));
        MinimumSize = new Size(LogicalToDeviceUnits(640), LogicalToDeviceUnits(480));
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        foreach (ColumnHeader c in _list.Columns) c.Width = LogicalToDeviceUnits(c.Width);

        _device = GpuDevice.Pick(GpuDevice.Enumerate(), null);
        if (_device == null)
        {
            _deviceLabel.Text = "No discrete GPU found";
            _start.Enabled = false;
            Log("No NVIDIA/AMD discrete GPU found. Display adapters present:");
            foreach (var g in GpuDevice.Enumerate()) Log($"  {g.Name}  {g.InstanceId}");
        }
        else
        {
            _deviceLabel.Text = _device.Name;
            Log($"Device: {_device.Name}");
            Log($"Instance: {_device.InstanceId}");
        }

        _start.Click += (_, _) => StartRecording();
        _stop.Click += async (_, _) => await StopRecordingAsync();
        _openReport.Click += (_, _) => { if (_reportPath != null) Shell(_reportPath); };
        _openFolder.Click += (_, _) => { Directory.CreateDirectory(DataRoot); Shell(_runDir ?? DataRoot); };
        _logToggle.Click += (_, _) => ToggleLog();
        _list.DoubleClick += (_, _) => { if (_reportPath != null) Shell(_reportPath); };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();

        Shown += (_, _) => { if (_device != null) StartRecording(); };
        FormClosing += OnClosing;
    }

    // ---------- layout ----------

    static Label StatValue() => new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 20f), Text = "–" };

    void BuildLayout()
    {
        var header = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        header.Controls.AddRange([_deviceLabel, _statePill]);

        var stats = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 4, Margin = new Padding(0, 10, 0, 6) };
        for (int i = 0; i < 4; i++) stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        AddStat(stats, 0, _events, "events captured");
        AddStat(stats, 1, _elapsed, "elapsed");
        AddStat(stats, 2, _wakes, "dGPU wakes (D3 → D0)");
        AddStat(stats, 3, _size, "trace file size");

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 0, 0, 6) };
        buttons.Controls.AddRange([_start, _stop, _openReport, _openFolder, _symbols]);

        _list.Columns.Add("Time", 100);
        _list.Columns.Add("Process", 200);
        _list.Columns.Add("PID", 70, HorizontalAlignment.Right);
        _list.Columns.Add("Reason", 65, HorizontalAlignment.Right);
        _list.Columns.Add("Awake for", 85, HorizontalAlignment.Right);
        _list.Columns.Add("Entry module", 140);
        _list.Columns.Add("Caller module", 180);

        var logHeader = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
        _logToggle.FlatAppearance.BorderSize = 0;
        logHeader.Controls.AddRange([_logToggle, _status]);

        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // header
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // stats
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // buttons
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));    // wake list
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // log header
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));    // log
        _root.Controls.Add(header, 0, 0);
        _root.Controls.Add(stats, 0, 1);
        _root.Controls.Add(buttons, 0, 2);
        _root.Controls.Add(_list, 0, 3);
        _root.Controls.Add(logHeader, 0, 4);
        _root.Controls.Add(_log, 0, 5);
        Controls.Add(_root);
    }

    static void AddStat(TableLayoutPanel t, int col, Label value, string caption)
    {
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        p.Controls.Add(value);
        p.Controls.Add(new Label { Text = caption, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 0) });
        t.Controls.Add(p, col, 0);
    }

    void ToggleLog()
    {
        bool show = !_log.Visible;
        _log.Visible = show;
        _logToggle.Text = show ? "▾ Log" : "▸ Log";
        _root.RowStyles[3] = show ? new RowStyle(SizeType.Percent, 55) : new RowStyle(SizeType.Percent, 100);
        _root.RowStyles[5] = show ? new RowStyle(SizeType.Percent, 45) : new RowStyle(SizeType.Absolute, 0);
    }

    // ---------- recording ----------

    void StartRecording()
    {
        if (_device == null || _busy || _live != null) return;
        try
        {
            Etw.StopStaleSessions();
            _runDir = Path.Combine(DataRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(_runDir);
            _reportPath = null;
            _openReport.Enabled = false;
            _list.Items.Clear();
            _liveRows.Clear();

            // Measure the state *before* tracing starts so the first D0 request can be classified.
            _initialState = _device.GetPowerState();
            var etlPath = Path.Combine(_runDir, "dgpu-wake.etl");
            new RecordingMeta(_device.InstanceId, _device.Name, _initialState, DateTime.Now).Save(etlPath);

            _recorder = new Recorder(etlPath);
            _live = new LiveMonitor(_device.InstanceId, _initialState);
            _live.Wake += w => BeginInvoke(() => OnLiveWake(w));
            _live.Sleep += (id, t, d) => BeginInvoke(() => OnLiveSleep(id, t, d));
            _live.WakeFailed += (id, status) => BeginInvoke(() => OnLiveWakeFailed(id, status));
            _live.Ignored += (t, proc, why) => BeginInvoke(() => Log($"[{t.ToLocalTime():HH:mm:ss.fff}] D0 request from {proc} not counted: {why}"));
            _live.NameResolved += (pid, name) => BeginInvoke(() =>
            {
                foreach (ListViewItem it in _list.Items)
                    if (it.Tag is LiveWake w && w.Pid == pid) it.SubItems[1].Text = name;
                Log($"  pid {pid} = {name}");
            });
            _live.Start();
            _startedAt = DateTime.Now;

            _start.Enabled = false;
            _stop.Enabled = true;
            Log($"Recording started → {_recorder.EtlPath}");
            Log($"dGPU was {StateName(_initialState)} when recording started. Waiting for wakes… (press Stop to build the report)");
            Log("Live view runs ~3 s behind so events can be put in order.");
        }
        catch (Exception ex)
        {
            Log($"ERROR starting trace: {ex.Message}");
            DisposeSessions();
        }
    }

    void OnLiveWake(LiveWake w)
    {
        var item = new ListViewItem([w.Time.ToLocalTime().ToString("HH:mm:ss.fff"), w.Process, w.Pid.ToString(), w.Reason?.ToString() ?? "", "…", "", ""])
        { Tag = w, ForeColor = Color.Firebrick };
        _liveRows[w.Id] = item;
        _list.Items.Add(item);
        item.EnsureVisible();
        Log($"[{w.Time.ToLocalTime():HH:mm:ss.fff}] WAKE  D3→D0  {w.Process} (pid {w.Pid}, tid {w.Tid}){(w.Reason is { } r ? $"  reason={r}" : "")}{(w.PriorStateKnown ? "" : "  (prior state unknown)")}");
    }

    void OnLiveSleep(int id, DateTime t, TimeSpan awake)
    {
        if (_liveRows.TryGetValue(id, out var item)) item.SubItems[4].Text = $"{awake.TotalSeconds:0.00} s";
        Log($"[{t.ToLocalTime():HH:mm:ss.fff}] SLEEP D0→D3  after {awake.TotalSeconds:0.00} s");
    }

    void OnLiveWakeFailed(int id, uint status)
    {
        if (_liveRows.TryGetValue(id, out var item)) { item.ForeColor = SystemColors.GrayText; item.SubItems[4].Text = "failed"; }
        Log($"  D0 request #{id} failed (status 0x{status:X8}); not counted");
    }

    async Task StopRecordingAsync()
    {
        if (_live == null || _recorder == null || _device == null || _busy) return;
        _busy = true;
        _stop.Enabled = false;
        var recorder = _recorder;
        int lost = recorder.EventsLost;
        Log("Stopping trace…");
        DisposeSessions();
        _timer.Stop();
        Tick();

        bool symbols = _symbols.Checked;
        var device = _device;
        int initialState = _initialState;
        var uiLog = new UiLogWriter(this);
        SetStatus(symbols ? "Analyzing trace and resolving symbols…" : "Analyzing trace…");
        try
        {
            var sw = Stopwatch.StartNew();
            var (analysis, html) = await Task.Run(() =>
            {
                var a = Analyzer.Analyze(recorder.EtlPath, device.InstanceId, initialState, symbols, uiLog);
                return (a, Report.Build(a, new ReportContext(device, recorder.EtlPath, lost, device.GetPowerState(), symbols)));
            });
            _reportPath = Path.Combine(_runDir!, "dgpu-wake-report.html");
            await File.WriteAllTextAsync(_reportPath, html);
            Log($"Analyzed {analysis.TotalEvents:N0} trace events in {sw.Elapsed.TotalSeconds:0.0} s: {analysis.CountedWakes.Count()} wake(s){(analysis.Ignored.Count > 0 ? $", {analysis.Ignored.Count} D0 request(s) not counted" : "")}{(lost > 0 ? $", {lost:N0} events LOST" : "")}.");
            Log($"Report → {_reportPath}");
            ShowAnalysis(analysis);
            _openReport.Enabled = true;
            Shell(_reportPath);
        }
        catch (Exception ex)
        {
            Log($"ERROR analyzing trace: {ex}");
        }
        finally
        {
            SetStatus("");
            _busy = false;
            _start.Enabled = true;
            _timer.Start();
        }
    }

    void ShowAnalysis(Analysis a)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var w in a.Wakes)
        {
            var awake = w.Failed ? "failed" : w.SleptAt is { } s ? $"{(s - w.Time).TotalSeconds:0.00} s" : "until end";
            var color = w.Failed ? SystemColors.GrayText : w.KernelInitiated ? Color.SteelBlue : Color.Firebrick;
            _list.Items.Add(new ListViewItem([w.Time.ToLocalTime().ToString("HH:mm:ss.fff"), w.ProcessName, w.Pid.ToString(),
                w.Reason?.ToString() ?? "", awake, w.EntryModule ?? "", w.CallerModule ?? ""]) { ForeColor = color });
        }
        _list.EndUpdate();
    }

    void DisposeSessions()
    {
        try { _live?.Dispose(); } catch { }
        try { _recorder?.Dispose(); } catch { }
        _live = null;
        _recorder = null;
    }

    void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_busy) { e.Cancel = true; SetStatus("Please wait for the analysis to finish."); return; }
        if (_live != null) Log("Closing: stopping trace without building a report.");
        DisposeSessions();
    }

    // ---------- periodic refresh ----------

    void Tick()
    {
        if (_device != null)
        {
            int s = _device.GetPowerState();
            _statePill.Text = StateName(s);
            (_statePill.BackColor, _statePill.ForeColor) = s switch
            {
                0 => (Color.FromArgb(224, 85, 61), Color.White),
                3 => (Color.FromArgb(31, 138, 76), Color.White),
                _ => (SystemColors.ControlDark, Color.White),
            };
        }
        if (_live != null)
        {
            _events.Text = _live.EventCount.ToString("N0");
            _wakes.Text = _live.WakeCount.ToString();
            var el = DateTime.Now - _startedAt;
            _elapsed.Text = el.TotalHours >= 1 ? el.ToString(@"h\:mm\:ss") : el.ToString(@"mm\:ss");
        }
        if (_recorder != null) _size.Text = FormatSize(_recorder.FileSize);
    }

    static string StateName(int s) => s switch { 0 => "D0 · awake", 3 => "D3 · asleep", -1 => "unknown", _ => $"D{s}" };
    static string FormatSize(long b) => b < 1 << 20 ? $"{b / 1024.0:0} KB" : $"{b / 1048576.0:0.0} MB";

    // ---------- log ----------

    public void Log(string line)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(line)); return; }
        if (_log.TextLength > 2_000_000) _log.Clear();
        _log.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
    }

    void SetStatus(string s) { if (InvokeRequired) BeginInvoke(() => _status.Text = s); else _status.Text = s; }

    static void Shell(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    sealed class UiLogWriter(MainForm form) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void WriteLine(string? value) => form.Log(value ?? "");
    }
}
