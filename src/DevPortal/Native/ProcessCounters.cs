using System.Runtime.InteropServices;

namespace DevPortal.Native;

/// <summary>
/// CPU time and working set of one PID through a single limited-information handle. System.Diagnostics.Process takes a
/// snapshot of every process on the machine per lookup, which costs milliseconds per PID on every scan.
/// </summary>
internal static class ProcessCounters
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")]
    private static extern bool GetProcessMemoryInfo(nint process, out MemoryCounters counters, int size);

    /// <summary>Null when the process is gone or cannot be opened; a field is null when only that call was refused.
    /// CPU is kernel + user time in ticks (100 ns).</summary>
    public static (long? CpuTicks, long? WorkingSetBytes)? Read(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == 0)
            return null;
        try
        {
            long? cpu = GetProcessTimes(handle, out _, out _, out var kernel, out var user) ? kernel + user : null;
            long? workingSet = GetProcessMemoryInfo(handle, out var memory, Marshal.SizeOf<MemoryCounters>()) ? (long)memory.WorkingSetSize : null;
            return (cpu, workingSet);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    {
        public int Cb;
        public int PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }
}
