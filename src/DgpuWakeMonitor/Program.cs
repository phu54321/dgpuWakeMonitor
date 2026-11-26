// Copyright (c) 2026 Park Hyunwoo
// SPDX-License-Identifier: MIT

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
            var sidecarDevice = meta != null ? new GpuDevice(meta.InstanceId, meta.DeviceName) : null;
            var adapters = GpuDevice.Enumerate();
            int di = Array.IndexOf(args, "--device");
            GpuDevice device;
            if (di > 0)
            {
                // An explicit selector must match; never silently fall back to another GPU.
                if (di + 1 >= args.Length) throw new ArgumentException("--device needs a value (name or instance-id substring).");
                var sel = args[di + 1];
                device = GpuDevice.Pick(adapters, sel)
                    ?? (sidecarDevice != null && GpuDevice.Pick([sidecarDevice], sel) != null ? sidecarDevice : null)
                    ?? throw new ArgumentException($"--device \"{sel}\" matches no display adapter. Available: "
                        + string.Join("; ", adapters.Append(sidecarDevice).OfType<GpuDevice>().DistinctBy(g => g.InstanceId)
                            .Select(g => $"{g.Name} ({g.InstanceId})")));
            }
            else
            {
                // The sidecar names the recorded device even if it is not present on this machine.
                device = (meta != null ? GpuDevice.Pick(adapters, meta.InstanceId) ?? sidecarDevice : GpuDevice.Pick(adapters, null))
                         ?? throw new InvalidOperationException("No discrete GPU found.");
            }
            bool symbols = args.Contains("--symbols");
            int initialState = meta != null && string.Equals(meta.InstanceId, device.InstanceId, StringComparison.OrdinalIgnoreCase)
                ? meta.InitialState : -1;
            log.WriteLine(initialState >= 0 ? $"meta: initial state D{initialState}" : "meta: initial state unknown for this device");
            var analysis = Analyzer.Analyze(args[1], device.InstanceId, initialState, symbols, log);
            File.WriteAllText(args[2], Report.Build(analysis, new ReportContext(device, args[1], device.GetPowerState(), symbols)));
            log.WriteLine($"OK: {analysis.CountedWakes.Count()} wakes, {analysis.Wakes.Count(w => w.Failed)} failed, {analysis.Ignored.Count} ignored, {analysis.TotalEvents} events, {analysis.EventsLost} lost, {analysis.DeviceIrps} device IRPs ({analysis.MissingStateIrps} without state), {analysis.PowerIrpEvents} IRP completions");
            foreach (var c in analysis.Caveats) log.WriteLine($"WARNING: {c}");
            return 0;
        }
        catch (ArgumentException ex) { log.WriteLine($"ERROR: {ex.Message}"); return 2; }
        catch (Exception ex) { log.WriteLine(ex); return 1; }
        finally { log.Dispose(); }
    }
}
