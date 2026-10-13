using System.Diagnostics;
using System.Management;
using DevPortal.Native;

namespace DevPortal.Discovery;

public sealed class ProcessInspector(ILogger<ProcessInspector> logger)
{
    public IReadOnlyDictionary<int, ProcessDetails> Inspect(IReadOnlySet<int> pids)
    {
        var result = new Dictionary<int, ProcessDetails>();
        if (pids.Count == 0)
            return result;

        foreach (var (pid, wmi) in QueryWmi(pids))
            result[pid] = wmi with { WorkingDirectory = ProcessCwd.TryGet(pid) };

        foreach (var pid in pids.Where(pid => !result.ContainsKey(pid)))
            result[pid] = Fallback(pid);

        return result;
    }

    private Dictionary<int, ProcessDetails> QueryWmi(IReadOnlySet<int> pids)
    {
        var result = new Dictionary<int, ProcessDetails>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, Name, CommandLine, ExecutablePath, CreationDate, SessionId FROM Win32_Process");
            foreach (var item in searcher.Get().Cast<ManagementObject>())
            {
                using (item)
                {
                    var pid = Convert.ToInt32(item["ProcessId"]);
                    if (!pids.Contains(pid))
                        continue;

                    result[pid] = new ProcessDetails(
                        pid,
                        Path.GetFileNameWithoutExtension(item["Name"] as string ?? $"pid {pid}"),
                        item["CommandLine"] as string,
                        item["ExecutablePath"] as string,
                        null,
                        ParseWmiDate(item["CreationDate"] as string),
                        item["SessionId"] is { } session ? Convert.ToInt32(session) : null);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            logger.LogWarning(ex, "WMI process query failed; falling back to System.Diagnostics");
        }
        return result;
    }

    private static ProcessDetails Fallback(int pid)
    {
        var name = pid switch { 0 => "Idle", 4 => "System", _ => $"pid {pid}" };
        DateTimeOffset? start = null;
        try
        {
            using var process = Process.GetProcessById(pid);
            name = process.ProcessName;
            start = process.StartTime;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        return new ProcessDetails(pid, name, null, null, null, start);
    }

    private static DateTimeOffset? ParseWmiDate(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        try
        {
            return ManagementDateTimeConverter.ToDateTime(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
