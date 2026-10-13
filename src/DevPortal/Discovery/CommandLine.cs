using System.Text;
using System.Text.RegularExpressions;

namespace DevPortal.Discovery;

internal static partial class CommandLine
{
    private static readonly string[] ProjectFlags = ["--project", "-p", "--app-dir", "--chdir", "--cwd", "--prefix", "-t", "--root"];
    private static readonly string[] PathExtensions = [".js", ".mjs", ".cjs", ".ts", ".py", ".php", ".dll", ".exe", ".csproj", ".jar", ".json"];

    public static IReadOnlyList<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var hasToken = false;

        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                quoted = !quoted;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (hasToken)
                    tokens.Add(current.ToString());
                current.Clear();
                hasToken = false;
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }
        }
        if (hasToken)
            tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>
    /// Paths mentioned in a command line, most specific first: explicit project flags, then path-like arguments.
    /// The executable itself (first token) is excluded; callers add it as the last resort.
    /// </summary>
    public static IReadOnlyList<string> CandidatePaths(string? commandLine, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return [];

        var tokens = Tokenize(commandLine);
        var explicitPaths = new List<string>();
        var otherPaths = new List<string>();

        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var separator = token.IndexOf('=');
            if (token.StartsWith('-') && separator > 0)
            {
                var flag = token[..separator];
                var value = token[(separator + 1)..];
                (ProjectFlags.Contains(flag) ? explicitPaths : otherPaths).Add(value);
            }
            else if (ProjectFlags.Contains(token) && i + 1 < tokens.Count)
            {
                explicitPaths.Add(tokens[++i]);
            }
            else if (!token.StartsWith('-'))
            {
                otherPaths.Add(token);
            }
        }

        return explicitPaths.Concat(otherPaths.Where(LooksLikePath))
            .Select(path => Resolve(path, workingDirectory))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string Redact(string commandLine) =>
        SecretPattern().Replace(commandLine, match => match.Groups["key"].Value + "***");

    private static bool LooksLikePath(string token) =>
        !token.Contains("://")
        && (token.Contains('\\') || token.Contains('/') || PathExtensions.Any(ext => token.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));

    private static string? Resolve(string path, string? workingDirectory)
    {
        try
        {
            if (Path.IsPathFullyQualified(path))
                return Path.GetFullPath(path);
            return workingDirectory is null ? null : Path.GetFullPath(Path.Combine(workingDirectory, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"(?<key>(password|passwd|pwd|secret|token|apikey|api[-_]key)\s*[=:]\s*)[^\s;""']+", RegexOptions.IgnoreCase)]
    private static partial Regex SecretPattern();
}
