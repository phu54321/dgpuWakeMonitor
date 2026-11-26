using System.Runtime.InteropServices;

namespace DgpuWakeMonitor;

/// <summary>A display adapter found through the Configuration Manager (CfgMgr32).</summary>
public sealed record GpuDevice(string InstanceId, string Name)
{
    const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    static readonly Guid DevPropGuid = new("a45c254e-df1c-4efd-8020-67d146a850e0");

    public string VendorId => InstanceId.Contains("VEN_", StringComparison.OrdinalIgnoreCase)
        ? InstanceId.Substring(InstanceId.IndexOf("VEN_", StringComparison.OrdinalIgnoreCase) + 4, 4).ToUpperInvariant()
        : "";

    public static List<GpuDevice> Enumerate()
    {
        var result = new List<GpuDevice>();
        const uint flags = 0x200 /*FILTER_CLASS*/ | 0x100 /*FILTER_PRESENT*/;
        if (CM_Get_Device_ID_List_SizeW(out uint len, DisplayClassGuid, flags) != 0 || len == 0) return result;
        var buf = new char[len];
        if (CM_Get_Device_ID_ListW(DisplayClassGuid, buf, len, flags) != 0) return result;
        foreach (var id in new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) continue;
            var name = GetStringProperty(id, 14) ?? GetStringProperty(id, 2) ?? id;
            result.Add(new GpuDevice(id, name));
        }
        return result;
    }

    /// <summary>Picks the discrete GPU: an explicit match, else NVIDIA, else AMD next to another adapter.</summary>
    public static GpuDevice? Pick(List<GpuDevice> all, string? match)
    {
        if (!string.IsNullOrEmpty(match))
            return all.FirstOrDefault(g => g.Name.Contains(match, StringComparison.OrdinalIgnoreCase)
                                        || g.InstanceId.Contains(match, StringComparison.OrdinalIgnoreCase));
        return all.FirstOrDefault(g => g.VendorId == "10DE")
            ?? (all.Count > 1 ? all.FirstOrDefault(g => g.VendorId == "1002") : null);
    }

    /// <summary>Current D-state (0..3) from DEVPKEY_Device_PowerData; -1 if unknown. Does not wake the device.</summary>
    public int GetPowerState()
    {
        var data = GetProperty(InstanceId, 32);
        if (data == null || data.Length < 8) return -1;
        int dps = BitConverter.ToInt32(data, 4); // DEVICE_POWER_STATE: D0=1 .. D3=4
        return dps is >= 1 and <= 4 ? dps - 1 : -1;
    }

    static string? GetStringProperty(string id, uint pid)
    {
        var data = GetProperty(id, pid);
        return data == null ? null : System.Text.Encoding.Unicode.GetString(data).TrimEnd('\0');
    }

    static byte[]? GetProperty(string id, uint pid)
    {
        if (CM_Locate_DevNodeW(out uint dev, id, 0) != 0) return null;
        var key = new DEVPROPKEY { fmtid = DevPropGuid, pid = pid };
        uint size = 0;
        CM_Get_DevNode_PropertyW(dev, ref key, out _, null, ref size, 0);
        if (size == 0) return null;
        var buf = new byte[size];
        return CM_Get_DevNode_PropertyW(dev, ref key, out _, buf, ref size, 0) == 0 ? buf : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_ID_List_SizeW(out uint len, string filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint len, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, byte[]? buffer, ref uint size, uint flags);
}
