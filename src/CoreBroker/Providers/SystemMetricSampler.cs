using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinWidgetBoard.Contracts.Protocol;

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
    /// False on the first tick after the sampler starts or wakes, and after the active IP
    /// interface set changes. Rate metrics are deltas, so without a like-for-like previous tick
    /// there is nothing honest to subtract from; the surfaces show a placeholder rather than a
    /// zero or a lifetime counter that reads as a real measurement.
    /// </summary>
    public bool HasBaseline { get; init; }
}

/// <summary>
/// One IP-layer interface counter snapshot. The interface ID is stable only while the interface
/// is present; a change in the set of IDs deliberately restarts the rate baseline.
/// </summary>
internal readonly record struct NetworkInterfaceCounterSnapshot(
    string InterfaceId,
    long ReceivedBytes,
    long SentBytes);

/// <summary>
/// The rate result for one sample. A missing baseline is distinct from a measured zero rate.
/// </summary>
internal readonly record struct NetworkRateMeasurement(
    double? UpBytesPerSecond,
    double? DownBytesPerSecond,
    bool HasBaseline);

/// <summary>
/// Converts per-interface cumulative byte counters to rates. Keeping the baseline per interface
/// is essential: an adapter that appears between ticks already has a lifetime counter, which
/// must never be mistaken for traffic from the current two-second window.
/// </summary>
internal sealed class NetworkRateTracker
{
    private readonly Dictionary<string, NetworkInterfaceCounterSnapshot> _previous =
        new(StringComparer.Ordinal);
    private DateTimeOffset? _previousSampledAtUtc;

    public void Reset()
    {
        _previous.Clear();
        _previousSampledAtUtc = null;
    }

    public NetworkRateMeasurement Sample(
        IReadOnlyList<NetworkInterfaceCounterSnapshot> counters,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(counters);

        var current = new Dictionary<string, NetworkInterfaceCounterSnapshot>(StringComparer.Ordinal);
        foreach (NetworkInterfaceCounterSnapshot counter in counters)
        {
            if (string.IsNullOrWhiteSpace(counter.InterfaceId) ||
                counter.ReceivedBytes < 0L ||
                counter.SentBytes < 0L ||
                !current.TryAdd(counter.InterfaceId, counter))
            {
                Reset();
                return default;
            }
        }

        if (current.Count == 0)
        {
            Reset();
            return default;
        }

        if (_previousSampledAtUtc is not { } previousAt || _previous.Count == 0)
        {
            StoreBaseline(current, nowUtc);
            return default;
        }

        double seconds = (nowUtc - previousAt).TotalSeconds;
        if (seconds <= 0d || current.Count != _previous.Count)
        {
            StoreBaseline(current, nowUtc);
            return default;
        }

        double receivedDelta = 0d;
        double sentDelta = 0d;
        foreach (KeyValuePair<string, NetworkInterfaceCounterSnapshot> pair in current)
        {
            if (!_previous.TryGetValue(pair.Key, out NetworkInterfaceCounterSnapshot previous) ||
                pair.Value.ReceivedBytes < previous.ReceivedBytes ||
                pair.Value.SentBytes < previous.SentBytes)
            {
                // This covers adapter additions, removals, counter resets and reconnects. All
                // make the aggregate incomparable with the previous tick.
                StoreBaseline(current, nowUtc);
                return default;
            }

            receivedDelta += pair.Value.ReceivedBytes - previous.ReceivedBytes;
            sentDelta += pair.Value.SentBytes - previous.SentBytes;
        }

        StoreBaseline(current, nowUtc);
        return new NetworkRateMeasurement(
            sentDelta / seconds,
            receivedDelta / seconds,
            HasBaseline: true);
    }

    private void StoreBaseline(
        IReadOnlyDictionary<string, NetworkInterfaceCounterSnapshot> counters,
        DateTimeOffset nowUtc)
    {
        _previous.Clear();
        foreach (KeyValuePair<string, NetworkInterfaceCounterSnapshot> pair in counters)
        {
            _previous.Add(pair.Key, pair.Value);
        }

        _previousSampledAtUtc = nowUtc;
    }
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
    private readonly NetworkRateTracker _networkRateTracker = new();

    private ulong _previousIdleTicks;
    private ulong _previousBusyTicks;
    private bool _hasCpuBaseline;
    private string _networkInterfaceId = SystemMonitorContract.AllNetworkInterfaces;
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
        _networkRateTracker.Reset();
        _counters?.ResetBaseline();
    }

    public SystemMetricSample Sample(DateTimeOffset nowUtc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Read before sampling: the samplers below establish the baselines they are missing,
        // so asking afterwards would always say yes, including on the very first tick.
        bool hasCpuBaseline = _hasCpuBaseline;

        double? cpuUsage = SampleCpuUsage();
        NetworkRateMeasurement networkRates = SampleNetworkRates(nowUtc);
        PdhReadings pdh = _counters?.Read() ?? default;
        (double? memoryUsed, double? memoryTotal) = ReadMemory();
        (double? diskUsed, double? diskTotal) = ReadSystemDriveSpace();

        return new SystemMetricSample
        {
            CpuUsagePercent = cpuUsage,
            // Not measurable from user mode on a hybrid CPU. Processor Frequency reports a
            // fixed nominal - 1.33 GHz on the reference machine while the part was actually
            // running near 4.2 GHz - and % Processor Performance times the registry's ~MHz
            // gives 8.18 GHz, because ~MHz records the boot frequency rather than the base.
            // A wrong number next to Task Manager is worse than an honest gap, so the clock
            // waits for the sensor service like the temperatures do.
            CpuClockMhz = null,
            MemoryUsedBytes = memoryUsed,
            MemoryTotalBytes = memoryTotal,
            GpuUsagePercent = pdh.GpuUsagePercent,
            GpuMemoryUsedBytes = pdh.GpuMemoryUsedBytes,
            GpuMemoryTotalBytes = _gpuMemoryTotalBytes,
            DiskBusyPercent = pdh.DiskBusyPercent,
            DiskUsedBytes = diskUsed,
            DiskTotalBytes = diskTotal,
            NetworkUpBytesPerSecond = networkRates.UpBytesPerSecond,
            NetworkDownBytesPerSecond = networkRates.DownBytesPerSecond,
            // Every sensor below needs a kernel driver; see the sensor service.
            CpuTemperatureCelsius = null,
            GpuTemperatureCelsius = null,
            FanRpm = null,
            HasBaseline = hasCpuBaseline && networkRates.HasBaseline,
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

    /// <summary>
    /// Which adapter the network rates are measured from. Empty sums every usable adapter.
    /// Changing it drops the baseline: the previous tick measured a different set of counters,
    /// so a delta across the change would report traffic that never happened on either source.
    /// </summary>
    public string NetworkInterfaceId
    {
        get => _networkInterfaceId;
        set
        {
            string next = SystemMonitorContract.NormalizeNetworkInterfaceId(value);
            if (string.Equals(_networkInterfaceId, next, StringComparison.Ordinal))
            {
                return;
            }

            _networkInterfaceId = next;
            _networkRateTracker.Reset();
        }
    }

    /// <summary>
    /// The adapters that can be selected as the network source, in the order the platform
    /// reports them. Deliberately the same filter the counters use: a list that offered an
    /// adapter the sampler would then ignore is a setting that silently does nothing.
    /// </summary>
    public static IReadOnlyList<SystemMonitorNetworkInterfaceDto> ListSelectableInterfaces()
    {
        var interfaces = new List<SystemMonitorNetworkInterfaceDto>();
        foreach (NetworkInterface adapter in EnumerateCountableAdapters())
        {
            if (!TryGetIpInterfaceId(adapter, out string interfaceId))
            {
                continue;
            }

            string name = adapter.Name;
            if (name.Length > SystemMonitorContract.MaxNetworkInterfaceNameLength)
            {
                name = name[..SystemMonitorContract.MaxNetworkInterfaceNameLength];
            }

            interfaces.Add(new SystemMonitorNetworkInterfaceDto
            {
                Id = interfaceId,
                Name = name,
            });
        }

        return interfaces;
    }

    private NetworkRateMeasurement SampleNetworkRates(DateTimeOffset nowUtc) =>
        _networkRateTracker.Sample(ReadNetworkCounters(_networkInterfaceId), nowUtc);

    /// <summary>
    /// Reads one counter per real IP-layer interface. Windows may expose the same physical NIC
    /// through one or more NDIS filter layers (WFP, QoS, capture filters); those layers often
    /// report the underlying byte counter too, but do not expose an IP interface index. Counting
    /// them would multiply one packet's traffic, so a usable IPv4 or IPv6 interface index is the
    /// boundary between a logical endpoint and a transport filter.
    /// </summary>
    private static List<NetworkInterfaceCounterSnapshot> ReadNetworkCounters(
        string networkInterfaceId)
    {
        var counters = new List<NetworkInterfaceCounterSnapshot>();
        var seenInterfaceIds = new HashSet<string>(StringComparer.Ordinal);
        bool selectOne = networkInterfaceId.Length > 0;
        foreach (NetworkInterface adapter in EnumerateCountableAdapters())
        {
            if (!TryGetIpInterfaceId(adapter, out string interfaceId) ||
                !seenInterfaceIds.Add(interfaceId) ||
                (selectOne &&
                    !string.Equals(interfaceId, networkInterfaceId, StringComparison.Ordinal)))
            {
                continue;
            }

            try
            {
                IPInterfaceStatistics statistics = adapter.GetIPStatistics();
                if (statistics.BytesReceived < 0L || statistics.BytesSent < 0L)
                {
                    continue;
                }

                counters.Add(
                    new NetworkInterfaceCounterSnapshot(
                        interfaceId,
                        statistics.BytesReceived,
                        statistics.BytesSent));
            }
            catch (NetworkInformationException)
            {
                // An adapter can disappear between enumeration and query. The tracker sees the
                // changed set and waits for a fresh baseline instead of inventing a rate.
            }
            catch (PlatformNotSupportedException)
            {
            }
        }

        return counters;
    }

    /// <summary>
    /// The adapters worth counting at all: up, and neither a loopback nor a tunnel. Shared by
    /// the counter read and the selectable list so the two can never disagree about what
    /// counts as an adapter.
    /// </summary>
    private static IEnumerable<NetworkInterface> EnumerateCountableAdapters()
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
        catch (PlatformNotSupportedException)
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

    private static bool TryGetIpInterfaceId(NetworkInterface adapter, out string interfaceId)
    {
        interfaceId = string.Empty;
        IPInterfaceProperties properties;
        try
        {
            properties = adapter.GetIPProperties();
        }
        catch (NetworkInformationException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }

        if (!HasUsableIpInterfaceIndex(properties))
        {
            return false;
        }

        interfaceId = adapter.Id;
        return !string.IsNullOrWhiteSpace(interfaceId);
    }

    private static bool HasUsableIpInterfaceIndex(IPInterfaceProperties properties)
    {
        try
        {
            if (properties.GetIPv4Properties().Index > 0)
            {
                return true;
            }
        }
        catch (NetworkInformationException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }

        try
        {
            return properties.GetIPv6Properties().Index > 0;
        }
        catch (NetworkInformationException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
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
