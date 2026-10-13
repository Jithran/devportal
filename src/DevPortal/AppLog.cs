namespace DevPortal;

/// <summary>A small rolling log file; the tray app has no console to write to. Silent until <see cref="Enable"/> is called.</summary>
public static class AppLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly Lock Gate = new();

    public static string DefaultPath => Path.Combine(AppInfo.DataDirectory, "devportal.log");

    public static string? FilePath { get; private set; }

    public static void Enable(string filePath) => FilePath = filePath;

    public static void Info(string message) => Write("INF", message, null);

    public static void Warn(string message, Exception? exception = null) => Write("WRN", message, exception);

    public static void Error(string message, Exception? exception = null) => Write("ERR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        if (FilePath is not { } path)
            return;
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {level} {message}{(exception is null ? "" : $" | {exception}")}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var file = new FileInfo(path);
                if (file.Exists && file.Length > MaxBytes)
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, line);
            }
            catch (IOException)
            {
            }
        }
    }
}

public sealed class AppLogLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new AppLogLogger(categoryName);

    public void Dispose()
    {
    }

    private sealed class AppLogLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var message = $"{category}: {formatter(state, exception)}";
            if (logLevel >= LogLevel.Error)
                AppLog.Error(message, exception);
            else
                AppLog.Warn(message, exception);
        }
    }
}
