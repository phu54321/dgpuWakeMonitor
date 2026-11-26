# dgpuWakeMonitor

Finds out **which process woke the discrete GPU** (D3 → D0) on hybrid-graphics laptops.
It records the same ETW data you'd capture with WPR, inside the app, and turns it into an HTML report
in the style of `powercfg /energy`.

## Usage

Run `dgpuWakeMonitor.exe` (it asks for administrator rights, which ETW needs). Recording starts right away:

- **events captured / elapsed / wakes / trace size** update live
- each wake appears in the list immediately (process, PID, DxgKrnl reason, time spent awake)
- the **Log** pane (collapsible) shows what's happening
- **Stop & build report** stops the trace, analyzes it, and opens `dgpu-wake-report.html`

Output goes to `%LOCALAPPDATA%\DgpuWakeMonitor\recordings\<timestamp>\`:
`dgpu-wake-report.html` plus `dgpu-wake.etl`, which you can open in Windows Performance Analyzer.

Tick **Resolve symbols** before stopping to resolve Windows frames (e.g. `win32u!NtGdiDdDDIQueryAdapterInfo`)
via the Microsoft symbol server (cached under `%LOCALAPPDATA%\DgpuWakeMonitor\symbols`; `_NT_SYMBOL_PATH` is honored).
NVIDIA user-mode DLLs have no public symbols, so they stay as `module+offset`.

Re-analyze an existing trace without the GUI:

```powershell
dgpuWakeMonitor --analyze <trace.etl> <report.html> [--symbols] [--device <name or instance-id substring>]
```

## How it works

Two ETW sessions run side by side:

| Session | Mode | Providers | Purpose |
|---|---|---|---|
| `DgpuWakeMonitor-Live` | real-time | Kernel-Power (0x8), DxgKrnl (0x400000), Kernel-Process (0x10) | live counter + wake list |
| `DgpuWakeMonitor-Record` | ETL file | kernel Process+ImageLoad, plus the above with stacks on event IDs 7 and 368 | full call stacks for the report |

The signal, checked against real traces on an RTX 5060 Laptop GPU:

- **Kernel-Power event 7 (`Irp` start)** with `MinorFunction=2` (SET_POWER), `PowerStateType=1` (device),
  `InstanceName` = the dGPU, and `PowerState=0` is the D0 wake request (`3` = back to D3).
  It fires **on the requesting thread**, so its PID/TID is the culprit.
- **DxgKrnl event 368 (`PowerRequestDuringD3`)** fires on the same thread just before it, with a `Reason` code.

The report's stacks come from the ETL, because real-time sessions only deliver the kernel half of the stack.
For example, `nvidia-smi` waking the GPU looks like this:

```text
nvidia-smi → nvml → win32u!NtGdiDdDDIQueryAdapterInfo → dxgkrnl!DxgkQueryAdapterInfo
  → nvlddmkm → dxgkrnl!DxgSetPowerComponentActiveCB → PoFxActivateComponent → D0 IRP
```

The current D-state shown in the header is read from `DEVPKEY_Device_PowerData`, which does not wake the device.

## Build

Requires the .NET 9 SDK and Windows 10/11 x64.

Single-file executable (recommended): produces `publish\dgpuWakeMonitor.exe`, about 7.8 MB:

```powershell
dotnet publish src/DgpuWakeMonitor -c Release -o publish
```

It needs the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) on the target machine.
For a fully standalone exe that needs no runtime (about 150 MB), add `--self-contained true`.
TraceEvent's native helpers (`msdia140.dll`, `KernelTraceControl.dll`) are bundled inside the exe and
extracted to a temp folder on first run.

Development build (multiple files under `bin\`):

```powershell
dotnet build src/DgpuWakeMonitor -c Release
```
