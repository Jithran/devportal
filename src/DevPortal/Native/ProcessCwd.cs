using System.Runtime.InteropServices;

namespace DevPortal.Native;

/// <summary>
/// Reads a process's current directory from its PEB. Only 64-bit targets are supported;
/// anything unreadable (protected, WOW64, access denied, exited) yields null.
/// </summary>
internal static class ProcessCwd
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessVmRead = 0x0010;
    private const int ProcessParametersOffset = 0x20;
    private const int CurrentDirectoryOffset = 0x38;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll")]
    private static extern bool IsWow64Process(nint process, out bool wow64);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint process, nint address, byte[] buffer, nint size, out nint read);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(nint process, int infoClass, ref ProcessBasicInformation info, int size, out int returned);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    public static string? TryGet(int pid)
    {
        if (!Environment.Is64BitProcess)
            return null;

        var handle = OpenProcess(ProcessQueryLimitedInformation | ProcessVmRead, false, pid);
        if (handle == 0)
            return null;

        try
        {
            if (IsWow64Process(handle, out var wow64) && wow64)
                return null;

            var info = new ProcessBasicInformation();
            if (NtQueryInformationProcess(handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0)
                return null;

            var parameters = ReadPointer(handle, info.PebBaseAddress + ProcessParametersOffset);
            if (parameters == 0)
                return null;

            var header = Read(handle, parameters + CurrentDirectoryOffset, 16);
            if (header is null)
                return null;

            var length = BitConverter.ToUInt16(header, 0);
            var buffer = (nint)BitConverter.ToInt64(header, 8);
            if (length == 0 || buffer == 0)
                return null;

            var chars = Read(handle, buffer, length);
            return chars is null ? null : Path.TrimEndingDirectorySeparator(System.Text.Encoding.Unicode.GetString(chars));
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static nint ReadPointer(nint handle, nint address)
    {
        var bytes = Read(handle, address, 8);
        return bytes is null ? 0 : (nint)BitConverter.ToInt64(bytes, 0);
    }

    private static byte[]? Read(nint handle, nint address, int size)
    {
        var buffer = new byte[size];
        return ReadProcessMemory(handle, address, buffer, size, out var read) && read == size ? buffer : null;
    }
}
