using System.Runtime.InteropServices;
using System.Text;

namespace Glassy.Core;

internal static class Native
{
    // ---- ntdll ----
    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int infoClass, IntPtr buf, int len, out int returned);
    public const int SystemProcessInformation = 5, SystemProcessorPerformanceInformation = 8;
    public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

    /// <summary>SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION: 48 bytes on x64. Kernel time includes idle time.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ProcPerf { public long Idle, Kernel, User, Dpc, Interrupt; public uint InterruptCount; }

    // ---- kernel32 ----
    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx s);

    // ---- volumes ----
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern uint GetDriveTypeW(string root);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetDiskFreeSpaceExW(string dir, out ulong available, out ulong total, out ulong free);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetVolumeInformationW(string root, StringBuilder name, uint nameSize, out uint serial, out uint maxComponent, out uint flags, StringBuilder fs, uint fsSize);
    [DllImport("kernel32.dll")] public static extern uint SetErrorMode(uint mode);
    public const uint SEM_FAILCRITICALERRORS = 1, FILE_READ_ONLY_VOLUME = 0x00080000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemCpuSetInformation(IntPtr buf, uint len, out uint returned, IntPtr process, uint flags);

    // ---- powrprof (battery) ----
    /// <summary>SYSTEM_BATTERY_STATE. Capacities and Rate are in mWh/mW; EstimatedTime (seconds) is filled by
    /// Windows only while discharging - there is no OS-provided time-to-full, so charging estimates it from Rate.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SystemBatteryState
    {
        public byte AcOnLine, BatteryPresent, Charging, Discharging;
        public byte Spare1_0, Spare1_1, Spare1_2, Spare1_3;
        public uint MaxCapacity, RemainingCapacity, Rate, EstimatedTime, DefaultAlert1, DefaultAlert2;
    }
    public const int SystemBatteryStateLevel = 5;
    [DllImport("powrprof.dll")]
    public static extern int CallNtPowerInformation(int level, IntPtr inBuf, uint inLen, ref SystemBatteryState outBuf, uint outLen);

    // ---- NVML (ships with the NVIDIA driver) ----
    [StructLayout(LayoutKind.Sequential)] public struct NvmlUtil { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)] public struct NvmlMem { public ulong Total, Free, Used; }
    [DllImport("nvml.dll")] public static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr dev);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetUtilizationRates(IntPtr dev, out NvmlUtil u);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetMemoryInfo(IntPtr dev, out NvmlMem m);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetEncoderUtilization(IntPtr dev, out uint util, out uint periodUs);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetDecoderUtilization(IntPtr dev, out uint util, out uint periodUs);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetPowerUsage(IntPtr dev, out uint milliwatts);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetClockInfo(IntPtr dev, uint clockType, out uint mhz);
    [DllImport("nvml.dll", CharSet = CharSet.Ansi)] public static extern int nvmlDeviceGetName(IntPtr dev, StringBuilder name, uint len);

    // ---- PDH ----
    [StructLayout(LayoutKind.Sequential)] public struct PdhDouble { public uint Status; public double Value; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhOpenQueryW(string src, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] public static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] public static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhDouble value);
    [DllImport("pdh.dll")] public static extern uint PdhCloseQuery(IntPtr query);
    [DllImport("pdh.dll")] public static extern uint PdhRemoveCounter(IntPtr counter);
    public const uint PDH_FMT_DOUBLE = 0x200;
}
