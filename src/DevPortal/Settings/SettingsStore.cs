using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevPortal.Settings;

/// <summary>Reads and writes <c>settings.json</c> in the Velopack install root, which updates leave alone.</summary>
public sealed class SettingsStore(string filePath)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private volatile UserSettings current = new();

    public static string DefaultPath => Path.Combine(AppInfo.DataDirectory, "settings.json");

    public string FilePath => filePath;

    public UserSettings Current => current;

    public UserSettings Load()
    {
        current = Read();
        return current;
    }

    public void Save(UserSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var temp = filePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
        File.Move(temp, filePath, overwrite: true);
        current = settings;
    }

    private UserSettings Read()
    {
        if (!File.Exists(filePath))
            return new UserSettings();
        try
        {
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(filePath), Json) ?? new UserSettings();
            if (SettingsValidator.Validate(settings, settings.Port, AnyPortFree.Instance).Count == 0)
                return settings;
            AppLog.Warn($"{filePath} has out-of-range values; using defaults.");
        }
        catch (JsonException ex)
        {
            AppLog.Warn($"{filePath} is not valid JSON; using defaults.", ex);
        }
        return new UserSettings();
    }

    private sealed class AnyPortFree : IPortAvailability
    {
        public static readonly AnyPortFree Instance = new();

        public bool IsFree(int port) => true;
    }
}
