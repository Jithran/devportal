namespace DevPortal.Updates;

/// <summary>The CHANGELOG.md embedded at build time, for the tray's release notes view.</summary>
public static class Changelog
{
    public static string Read()
    {
        using var stream = typeof(Changelog).Assembly.GetManifestResourceStream("CHANGELOG.md");
        if (stream is null)
            return "";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The body under <c>## vX.Y.Z</c> (the heading may continue with a date). "1.0.1" never matches "1.0.10".</summary>
    public static string Section(string changelog, string version)
    {
        var heading = $"## v{version.TrimStart('v')}";
        var lines = new List<string>();
        var inSection = false;
        foreach (var raw in changelog.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                inSection = line == heading || line.StartsWith(heading + " ", StringComparison.Ordinal);
                continue;
            }
            if (inSection)
                lines.Add(line);
        }
        return string.Join(Environment.NewLine, lines).Trim();
    }
}
