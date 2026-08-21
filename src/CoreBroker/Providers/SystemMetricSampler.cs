using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// One tick of raw hardware readings. Every field is nullable and every null means "this
/// machine did not supply it", never zero: a fan reading of 0 RPM and a fan we cannot see are
/// different facts, and the surfaces must be able to tell them apart.
/// </summary>
public sealed record SystemMetricSample
{
    public double? CpuUsagePercent { get; init; }

    public double? CpuClockMhz { get; init; }

    public double? CpuTemperatureCelsius { get; init; }

    public double? MemoryUsedBytes { get; init; }

    public double? MemoryTotalBytes { get; init; }

    public double? GpuUsagePercent { get; init; }

    public double? GpuMemoryUsedBytes { get; init; }

    public double? GpuMemoryTotalBytes { get; init; }

    public double? GpuTemperatureCelsius { get; init; }

    public double? DiskBusyPercent { get; init; }

    public double? DiskUsedBytes { get; init; }

    public double? DiskTotalBytes { get; init; }

    public double? NetworkUpBytesPerSecond { get; init; }

    public double? NetworkDownBytesPerSecond { get; init; }

    public double? FanRpm { get; init; }

    /// <summary>
    /// False on the first tick after the sampler starts or wakes. Rate metrics are deltas, so
    /// without a previous tick there is nothing to subtract from; the surfaces show a
    /// placeholder for one tick rather than a zero that reads as a real measurement.
    /// </summary>
    public bool HasBaseline { get; init; }
}

/// <summary>
/// Samples what Windows exposes to a normal, unelevated process: system times, memory status,
/// interface counters, drive space, and the PDH counter set. Temperature, fan speed and any
/// other sensor behind a kernel driver are deliberately absent here - they arrive, if at all,
/// from the optional sensor service, and this type reports them as null in the meantime.
/// </summary>
public sealed class SystemMetricSampler : IDisposable
{
    private readonly PdhCounterSet? _counters;
    private readonly double? _gpuMemoryTotalBytes;

    private ulong _previousIdleTicks;
    private ulong _previousBusyTicks;
    private long _previousNetworkReceivedBytes;
    private long _previousNetworkSentBytes;
    private DateTimeOffset? _previousSampledAtUtc;
    private bool _hasCpuBaseline;
    private bool _hasNetworkBaseline;
    private bool _disposed;

    public SystemMetricSampler()
    {
        // A machine without the GPU counter set, or with PDH unavailable, still reports CPU,
        // memory, disk and network. Losing one family must not lose the rest.
        _counters = PdhCounterSet.TryCreate();
        _gpuMemoryTotalBytes = ReadGpuMemoryTotalBytes();
    }

    /// <summary>
    /// Drops the deltas. Called when the provider wakes from idle: the counters kept running
    /// while nobody was watching, so the first delta after a pause would cover the whole pause
    /// and report a meaningless average.
    /// </summary>
    public void ResetBaseline()
    {
        _hasCpuBaseline = false;
        _hasNetworkBaseline = false;
        _previousSampledAtUtc = null;
        _counters?.ResetBaseline();
    }

    public SystemMetricSample Sample(DateTimeOffset nowUtc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Read before sampling: the samplers below establish the baselines they are missing,
        // so asking afterwards would always say yes, including on the very first tick.
        bool hasBaseline = _hasCpuBaseline &&
            _hasNetworkBaseline &&
            _previousSampledAtUtc is not null;

        double? cpuUsage = SampleCpuUsage();
        (double? upRate, double? downRate) = SampleNetworkRates(nowUtc);
        PdhReadings pdh = _counters?.Read() ?? default;
        (double? memoryUsed, double? memoryTotal) = ReadMemory();
        (double? diskUsed, double? diskTotal) = ReadSystemDriveSpace();

        _previousSampledAtUtc = nowUtc;

        return new SystemMetricSample
        {
            CpuUsagePercent = cpuUsage,
            CpuClockMhz = pdh.CpuClockMhz,
            MemoryUsedBytes = memoryUsed,
            MemoryTotalBytes = memoryTotal,
            GpuUsagePercent = pdh.GpuUsagePercent,
            GpuMemoryUsedBytes = pdh.GpuMemoryUsedBytes,
            GpuMemoryTotalBytes = _gpuMemoryTotalBytes,
            DiskBusyPercent = pdh.DiskBusyPercent,
            DiskUsedBytes = diskUsed,
            DiskTotalBytes = diskTotal,
            NetworkUpBytesPerSecond = upRate,
            NetworkDownBytesPerSecond = downRate,
            // Every sensor below needs a kernel driver; see the sensor service.
            CpuTemperatureCelsius = null,
            GpuTemperatureCelsius = null,
            FanRpm = null,
            HasBaseline = hasBaseline,
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _counters?.Dispose();
    }

    private double? SampleCpuUsage()
    {
        if (!NativeMethods.GetSystemTimes(
                out NativeMethods.FileTime idle,
                out NativeMethods.FileTime kernel,
                out NativeMethods.FileTime user))
        {
            return null;
        }

        // Kernel time includes idle time, so busy is kernel + user - idle.
        ulong idleTicks = idle.ToUInt64();
        ulong totalTicks = kernel.ToUInt64() + user.ToUInt64();
        ulong busyTicks = totalTicks >= idleTicks ? totalTicks - idleTicks : 0UL;

        if (!_hasCpuBaseline)
        {
            _previousIdleTicks = idleTicks;
            _previousBusyTicks = busyTicks;
            _hasCpuBaseline = true;
            return null;
        }

        ulong idleDelta = idleTicks - _previousIdleTicks;
        ulong busyDelta = busyTicks - _previousBusyTicks;
        _previousIdleTicks = idleTicks;
        _previousBusyTicks = busyTicks;

        ulong totalDelta = idleDelta + busyDelta;
        if (totalDelta == 0UL)
        {
            return null;
        }

        return Math.Clamp(busyDelta * 100d / totalDelta, 0d, 100d);
    }

    private (double? UpBytesPerSecond, double? DownBytesPerSecond) SampleNetworkRates(
        DateTimeOffset nowUtc)
    {
        long received = 0;
        long sent = 0;
        bool sawInterface = false;

        foreach (NetworkInterface adapter in EnumerateInterfaces())
        {
            try
            {
                IPInterfaceStatistics statistics = adapter.GetIPStatistics();
                received += statistics.BytesReceived;
                sent += statistics.BytesSent;
                sawInterface = true;
            }
            catch (NetworkInformationException)
            {
                // An adapter can disappear between enumeration and query; the remaining ones
                // still produce a usable total.
            }
            catch (PlatformNotSupportedException)
            {
            }
        }

        if (!sawInterface)
        {
            return (null, null);
        }

        if (!_hasNetworkBaseline || _previousSampledAtUtc is not { } previousAt)
        {
            _previousNetworkReceivedBytes = received;
            _previousNetworkSentBytes = sent;
            _hasNetworkBaseline = true;
            return (null, null);
        }

        double seconds = (nowUtc - previousAt).TotalSeconds;
        long receivedDelta = received - _previousNetworkReceivedBytes;
        long sentDelta = sent - _previousNetworkSentBytes;
        _previousNetworkReceivedBytes = received;
        _previousNetworkSentBytes = sent;

        // An adapter that went away takes its counter with it, which shows up as a negative
        // delta. Report nothing for that tick rather than a negative rate.
        if (seconds <= 0d || receivedDelta < 0 || sentDelta < 0)
        {
            return (null, null);
        }

        return (sentDelta / seconds, receivedDelta / seconds);
    }

    private static IEnumerable<NetworkInterface> EnumerateInterfaces()
    {
        NetworkInterface[] adapters;
        try
        {
            adapters = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            yield break;
        }

        foreach (NetworkInterface adapter in adapters)
        {
            if (adapter.OperationalStatus == OperationalStatus.Up &&
                adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                adapter.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            {
                yield return adapter;
            }
        }
    }

    private static (double? UsedBytes, double? TotalBytes) ReadMemory()
    {
        var status = new NativeMethods.MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<NativeMethods.MemoryStatusEx>(),
        };
        if (!NativeMethods.GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0UL)
        {
            return (null, null);
        }

        return (status.TotalPhys - status.AvailPhys, status.TotalPhys);
    }

    private static (double? UsedBytes, double? TotalBytes) ReadSystemDriveSpace()
    {
        try
        {
            string root = Path.GetPathRoot(Environment.SystemDirectory) ?? string.Empty;
            if (root.Length == 0)
            {
                return (null, null);
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.TotalSize <= 0L)
            {
                return (null, null);
            }

            return (drive.TotalSize - drive.TotalFreeSpace, drive.TotalSize);
        }
        catch (ArgumentException)
        {
            return (null, null);
        }
        catch (IOException)
        {
            return (null, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Total dedicated video memory. PDH reports usage but never the ceiling, and the display
    /// class key is the only place a normal process can read it without bringing in DXGI.
    /// </summary>
    private static double? ReadGpuMemoryTotalBytes()
    {
        const string DisplayClassKey =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        try
        {
            using RegistryKey? displayClass = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (displayClass is null)
            {
                return null;
            }

            double largest = 0d;
            foreach (string name in displayClass.GetSubKeyNames())
            {
                // One unreadable adapter subkey must not cost the readings of the others, so
                // the guard is per subkey rather than around the whole scan.
                try
                {
                    using RegistryKey? adapter = displayClass.OpenSubKey(name);
                    object? size = adapter?.GetValue("HardwareInformation.qwMemorySize");
                    double bytes = size switch
                    {
                        long value when value > 0L => value,
                        int value when value > 0 => value,
                        byte[] raw when raw.Length == 8 => BitConverter.ToInt64(raw, 0),
                        _ => 0d,
                    };

                    largest = Math.Max(largest, bytes);
                }
                catch (System.Security.SecurityException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (IOException)
                {
                }
            }

            return largest > 0d ? largest : null;
        }
        catch (System.Security.SecurityException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct FileTime
        {
            public uint LowDateTime;
            public uint HighDateTime;

            public readonly ulong ToUInt64() =>
                ((ulong)HighDateTime << 32) | LowDateTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemTimes(
            out FileTime idleTime,
            out FileTime kernelTime,
            out FileTime userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
    }
}
