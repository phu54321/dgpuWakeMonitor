namespace DgpuWakeMonitor;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Headless re-analysis of an existing trace: dgpuWakeMonitor --analyze <trace.etl> <report.html> [--symbols] [--device <match>]
        if (args.Length >= 3 && args[0] == "--analyze")
            return AnalyzeHeadless(args);

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Crash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash(e.ExceptionObject as Exception);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
        return 0;
    }

    static void Crash(Exception? ex)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DgpuWakeMonitor");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "crash.log");
        File.AppendAllText(path, $"[{DateTime.Now:O}] {ex}\n\n");
        MessageBox.Show($"{ex?.Message}\n\nDetails written to {path}", "dgpuWakeMonitor error", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    static int AnalyzeHeadless(string[] args)
    {
        var log = new StreamWriter(Path.ChangeExtension(args[2], ".log")) { AutoFlush = true };
        try
        {
            // The recording's sidecar knows the device and its state when tracing started.
            var meta = RecordingMeta.TryLoad(args[1]);
            int di = Array.IndexOf(args, "--device");
            var match = di > 0 && di + 1 < args.Length ? args[di + 1] : meta?.InstanceId;
            var device = GpuDevice.Pick(GpuDevice.Enumerate(), match)
                         ?? (meta != null ? new GpuDevice(meta.InstanceId, meta.DeviceName) : null)
                         ?? throw new InvalidOperationException("No discrete GPU found.");
            bool symbols = args.Contains("--symbols");
            int initialState = meta != null && string.Equals(meta.InstanceId, device.InstanceId, StringComparison.OrdinalIgnoreCase)
                ? meta.InitialState : -1;
            log.WriteLine(meta != null ? $"meta: initial state D{initialState}" : "meta: none (initial state will be inferred)");
            var analysis = Analyzer.Analyze(args[1], device.InstanceId, initialState, symbols, log);
            File.WriteAllText(args[2], Report.Build(analysis, new ReportContext(device, args[1], 0, device.GetPowerState(), symbols)));
            log.WriteLine($"OK: {analysis.CountedWakes.Count()} wakes, {analysis.Wakes.Count(w => w.Failed)} failed, {analysis.Ignored.Count} ignored, {analysis.TotalEvents} events");
            return 0;
        }
        catch (Exception ex) { log.WriteLine(ex); return 1; }
        finally { log.Dispose(); }
    }
}
