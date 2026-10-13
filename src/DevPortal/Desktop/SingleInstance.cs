namespace DevPortal.Desktop;

/// <summary>
/// One DevPortal per user session. A second launch wakes the first through a named event (local IPC, no network)
/// and exits; the first then opens the portal.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\DevPortal.Instance";
    private const string ActivateEventName = @"Local\DevPortal.Activate";

    private readonly Mutex mutex;
    private readonly EventWaitHandle activate;
    private RegisteredWaitHandle? registration;

    private SingleInstance(Mutex mutex)
    {
        this.mutex = mutex;
        activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
    }

    /// <param name="wait">How long to wait for a previous instance to exit, e.g. right after an update restart.</param>
    public static SingleInstance? TryAcquire(TimeSpan wait)
    {
        var mutex = new Mutex(false, MutexName);
        try
        {
            if (mutex.WaitOne(wait))
                return new SingleInstance(mutex);
        }
        catch (AbandonedMutexException)
        {
            return new SingleInstance(mutex);
        }
        mutex.Dispose();
        return null;
    }

    public static void ActivateExisting()
    {
        if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var existing))
        {
            using (existing)
                existing.Set();
        }
    }

    public void OnActivated(Action callback) =>
        registration = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) => callback(), null, Timeout.Infinite, executeOnlyOnce: false);

    public void Dispose()
    {
        registration?.Unregister(null);
        activate.Dispose();
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
