using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// The PDH readings taken in one tick. Null means the counter is absent on this machine or has
/// not produced a value yet, never zero.
/// </summary>
internal readonly record struct PdhReadings
{
    public double? CpuClockMhz { get; init; }

    public double? DiskBusyPercent { get; init; }

    public double? GpuUsagePercent { get; init; }

    public double? GpuMemoryUsedBytes { get; init; }
}

/// <summary>
/// The performance counters the hardware monitor reads, opened once and collected per tick.
///
/// Counters are added with <c>PdhAddEnglishCounter</c> rather than <c>PdhAddCounter</c>: counter
/// paths are localized, and on a non-English Windows the English path silently fails to resolve
/// with the localizing variant. The reference machine runs zh-CN, so this is not hypothetical.
/// </summary>
internal sealed class PdhCounterSet : IDisposable
{
    // Only x64 is configured for this solution, so the interop struct layouts below assume
    // 8-byte pointers and 8-byte alignment of the value union.
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhFmtNoCap100 = 0x00008000;
    private const uint ErrorSuccess = 0;
    private const uint PdhMoreData = 0x800007D2;

    // Processor Frequency reports the current clock in MHz directly. The obvious
    // alternative - % Processor Performance times the registry's ~MHz - is wrong on a hybrid
    // CPU: ~MHz records whatever the boot frequency happened to be, so on the reference
    // machine (Core Ultra 5 225H) that product read 8.18 GHz.
    private const string CpuFrequencyPath =
        @"\Processor Information(_Total)\Processor Frequency";
    private const string DiskIdlePath = @"\PhysicalDisk(_Total)\% Idle Time";
    private const string GpuUtilizationPath = @"\GPU Engine(*)\Utilization Percentage";
    // An integrated GPU holds everything in shared memory and reports zero dedicated usage, so
    // both families are summed; on a discrete card the dedicated side dominates instead.
    private const string GpuDedicatedMemoryPath = @"\GPU Adapter Memory(*)\Dedicated Usage";
    private const string GpuSharedMemoryPath = @"\GPU Adapter Memory(*)\Shared Usage";

    private readonly IntPtr _query;
    private readonly IntPtr _cpuFrequency;
    private readonly IntPtr _diskIdle;
    private readonly IntPtr _gpuUtilization;
    private readonly IntPtr _gpuDedicatedMemory;
    private readonly IntPtr _gpuSharedMemory;

    private bool _needsPrime = true;
    private bool _disposed;

    private PdhCounterSet(
        IntPtr query,
        IntPtr cpuFrequency,
        IntPtr diskIdle,
        IntPtr gpuUtilization,
        IntPtr gpuDedicatedMemory,
        IntPtr gpuSharedMemory)
    {
        _query = query;
        _cpuFrequency = cpuFrequency;
        _diskIdle = diskIdle;
        _gpuUtilization = gpuUtilization;
        _gpuDedicatedMemory = gpuDedicatedMemory;
        _gpuSharedMemory = gpuSharedMemory;
    }

    /// <summary>
    /// Opens the query, or returns null when PDH itself is unavailable. Individual counters are
    /// allowed to be missing - a machine with no GPU counter set still reports CPU and disk.
    /// </summary>
    public static PdhCounterSet? TryCreate()
    {
        if (NativeMethods.PdhOpenQueryW(null, IntPtr.Zero, out IntPtr query) != ErrorSuccess)
        {
            return null;
        }

        IntPtr cpuFrequency = TryAddCounter(query, CpuFrequencyPath);
        IntPtr diskIdle = TryAddCounter(query, DiskIdlePath);
        IntPtr gpuUtilization = TryAddCounter(query, GpuUtilizationPath);
        IntPtr gpuDedicatedMemory = TryAddCounter(query, GpuDedicatedMemoryPath);
        IntPtr gpuSharedMemory = TryAddCounter(query, GpuSharedMemoryPath);

        if (cpuFrequency == IntPtr.Zero &&
            diskIdle == IntPtr.Zero &&
            gpuUtilization == IntPtr.Zero &&
            gpuDedicatedMemory == IntPtr.Zero &&
            gpuSharedMemory == IntPtr.Zero)
        {
            CloseQuery(query);
            return null;
        }

        return new PdhCounterSet(
            query,
            cpuFrequency,
            diskIdle,
            gpuUtilization,
            gpuDedicatedMemory,
            gpuSharedMemory);
    }

    /// <summary>
    /// Discards the counters' own previous sample. PDH keeps collecting nothing while the query
    /// is idle, so the first delta after a pause would otherwise average across the entire
    /// pause and report a figure that describes no moment in particular.
    /// </summary>
    public void ResetBaseline() => _needsPrime = true;

    public PdhReadings Read()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (NativeMethods.PdhCollectQueryData(_query) != ErrorSuccess)
        {
            return default;
        }

        if (_needsPrime)
        {
            // Rate counters need two collections before the first difference exists.
            _needsPrime = false;
            return default;
        }

        double? diskIdle = ReadSingle(_diskIdle);
        return new PdhReadings
        {
            CpuClockMhz = ReadSingle(_cpuFrequency),
            DiskBusyPercent = diskIdle is { } idle
                ? Math.Clamp(100d - idle, 0d, 100d)
                : null,
            GpuUsagePercent = ReadGpuUtilization(),
            GpuMemoryUsedBytes = Add(
                ReadArraySum(_gpuDedicatedMemory),
                ReadArraySum(_gpuSharedMemory)),
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_query != IntPtr.Zero)
        {
            CloseQuery(_query);
        }
    }

    // Nothing can be done about a query that refuses to close, but the status is asserted
    // rather than dropped so a debug run surfaces it.
    private static void CloseQuery(IntPtr query) =>
        Debug.Assert(
            NativeMethods.PdhCloseQuery(query) == ErrorSuccess,
            "PdhCloseQuery failed.");

    private static IntPtr TryAddCounter(IntPtr query, string path) =>
        NativeMethods.PdhAddEnglishCounterW(
            query,
            path,
            IntPtr.Zero,
            out IntPtr counter) == ErrorSuccess
            ? counter
            : IntPtr.Zero;

    private static double? ReadSingle(IntPtr counter)
    {
        if (counter == IntPtr.Zero)
        {
            return null;
        }

        uint status = NativeMethods.PdhGetFormattedCounterValue(
            counter,
            PdhFmtDouble | PdhFmtNoCap100,
            out _,
            out NativeMethods.PdhFmtCounterValueDouble value);
        return status == ErrorSuccess && value.CStatus == ErrorSuccess &&
            double.IsFinite(value.DoubleValue)
            ? value.DoubleValue
            : null;
    }

    /// <summary>
    /// Task Manager's GPU figure: instances are summed inside each engine type, and the busiest
    /// engine type wins. Summing every instance instead would double-count a frame that keeps
    /// the 3D and the copy engine busy at once and routinely exceed 100%.
    /// </summary>
    private double? ReadGpuUtilization()
    {
        var byEngineType = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (!ReadArray(
                _gpuUtilization,
                (instance, value) =>
                {
                    string engineType = ExtractEngineType(instance);
                    byEngineType[engineType] =
                        byEngineType.GetValueOrDefault(engineType) + value;
                }))
        {
            return null;
        }

        if (byEngineType.Count == 0)
        {
            return null;
        }

        return Math.Clamp(byEngineType.Values.Max(), 0d, 100d);
    }

    private static string ExtractEngineType(string instanceName)
    {
        const string Marker = "engtype_";
        int index = instanceName.LastIndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        return index < 0
            ? instanceName
            : instanceName[(index + Marker.Length)..];
    }

    // Null plus a value is that value: one memory family missing must not lose the other.
    private static double? Add(double? left, double? right) =>
        left is null && right is null ? null : (left ?? 0d) + (right ?? 0d);

    private static double? ReadArraySum(IntPtr counter)
    {
        double total = 0d;
        return ReadArray(counter, (_, value) => total += value) ? total : null;
    }

    private static bool ReadArray(IntPtr counter, Action<string, double> onItem)
    {
        if (counter == IntPtr.Zero)
        {
            return false;
        }

        uint bufferSize = 0;
        uint itemCount = 0;
        uint status = NativeMethods.PdhGetFormattedCounterArrayW(
            counter,
            PdhFmtDouble | PdhFmtNoCap100,
            ref bufferSize,
            out itemCount,
            IntPtr.Zero);
        if (status != PdhMoreData || bufferSize == 0 || itemCount == 0)
        {
            return false;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            status = NativeMethods.PdhGetFormattedCounterArrayW(
                counter,
                PdhFmtDouble | PdhFmtNoCap100,
                ref bufferSize,
                out itemCount,
                buffer);
            if (status != ErrorSuccess)
            {
                return false;
            }

            int itemSize = Marshal.SizeOf<NativeMethods.PdhFmtCounterValueItemDouble>();
            for (uint index = 0; index < itemCount; index++)
            {
                var item = Marshal.PtrToStructure<NativeMethods.PdhFmtCounterValueItemDouble>(
                    buffer + (int)(index * itemSize));
                if (item.CStatus != ErrorSuccess || !double.IsFinite(item.DoubleValue))
                {
                    continue;
                }

                string name = item.SzName == IntPtr.Zero
                    ? string.Empty
                    : Marshal.PtrToStringUni(item.SzName) ?? string.Empty;
                onItem(name, item.DoubleValue);
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct PdhFmtCounterValueDouble
        {
            public uint CStatus;
            public double DoubleValue;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PdhFmtCounterValueItemDouble
        {
            public IntPtr SzName;
            public uint CStatus;
            public double DoubleValue;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint PdhOpenQueryW(
            string? dataSource,
            IntPtr userData,
            out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint PdhAddEnglishCounterW(
            IntPtr query,
            string fullCounterPath,
            IntPtr userData,
            out IntPtr counter);

        [DllImport("pdh.dll", ExactSpelling = true)]
        internal static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", ExactSpelling = true)]
        internal static extern uint PdhGetFormattedCounterValue(
            IntPtr counter,
            uint format,
            out uint counterType,
            out PdhFmtCounterValueDouble value);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint PdhGetFormattedCounterArrayW(
            IntPtr counter,
            uint format,
            ref uint bufferSize,
            out uint itemCount,
            IntPtr itemBuffer);

        [DllImport("pdh.dll", ExactSpelling = true)]
        internal static extern uint PdhCloseQuery(IntPtr query);
    }
}
