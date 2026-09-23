using System.Runtime.InteropServices;

namespace Glassy.Core;

/// <summary>One battery reading. Multiple batteries (rare - some laptops split cells across two bays) are combined
/// into this single reading by CallNtPowerInformation itself, which reports the system-wide aggregate.</summary>
public struct BatteryStatus
{
    public bool Present;
    public bool Charging;
    public bool OnAc;
    public double Percent;          // 0-100
    /// <summary>Time to empty (discharging) or an estimate of time to full from the current charge rate
    /// (charging) - null when unknown (rate not reported, or already full).</summary>
    public TimeSpan? TimeRemaining;
}

public interface IBatteryProvider : IDisposable
{
    /// <summary>null when this machine has no battery at all.</summary>
    BatteryStatus? Snapshot { get; }
}

/// <summary>Reads CallNtPowerInformation(SystemBatteryState) fresh on every call - it's a cheap syscall, no need
/// to poll it on its own thread the way drives/network are polled.</summary>
public sealed class SystemBatteryProvider : IBatteryProvider
{
    public BatteryStatus? Snapshot
    {
        get
        {
            var s = new Native.SystemBatteryState();
            if (Native.CallNtPowerInformation(Native.SystemBatteryStateLevel, IntPtr.Zero, 0, ref s, (uint)Marshal.SizeOf<Native.SystemBatteryState>()) != 0) return null;
            if (s.BatteryPresent == 0) return null;
            double percent = s.MaxCapacity == 0 ? 0 : 100.0 * s.RemainingCapacity / s.MaxCapacity;
            TimeSpan? remaining = null;
            if (s.Discharging != 0 && s.EstimatedTime != uint.MaxValue && s.EstimatedTime > 0)
                remaining = TimeSpan.FromSeconds(s.EstimatedTime);
            else if (s.Charging != 0 && s.Rate > 0 && s.MaxCapacity > s.RemainingCapacity)
                remaining = TimeSpan.FromHours((s.MaxCapacity - s.RemainingCapacity) / (double)s.Rate);
            return new BatteryStatus
            {
                Present = true, Charging = s.Charging != 0, OnAc = s.AcOnLine != 0,
                Percent = Math.Clamp(percent, 0, 100), TimeRemaining = remaining,
            };
        }
    }
    public void Dispose() { }
}

/// <summary>Fixed reading for tests and for --demo on machines (like the dev box) with no real battery.</summary>
public sealed class FakeBatteryProvider : IBatteryProvider
{
    public BatteryStatus? Value;
    public BatteryStatus? Snapshot => Value;
    public void Dispose() { }
}
