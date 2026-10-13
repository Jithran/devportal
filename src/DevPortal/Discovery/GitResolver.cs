using System.Collections.Concurrent;

namespace DevPortal.Discovery;

/// <summary>
/// Resolves git metadata by reading .git files directly. Shelling out to git would trip over
/// "dubious ownership" checks when the service runs as LocalSystem against user-owned repos.
/// </summary>
public sealed class GitResolver(TimeProvider time)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, GitInfo? Info)> cache = new(StringComparer.OrdinalIgnoreCase);

    public GitInfo? Resolve(string? path)
    {
        var directory = ToDirectory(path);
        if (directory is null)
            return null;

        var now = time.GetUtcNow();
        if (cache.TryGetValue(directory, out var cached) && now - cached.At < CacheDuration)
            return cached.Info;

        var info = ResolveUncached(directory);
        cache[directory] = (now, info);
        return info;
    }

    private static string? ToDirectory(string? path)
    {
        // A relative path would silently resolve against DevPortal's own working directory.
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return null;
        try
        {
            var full = Path.GetFullPath(path);
            if (Directory.Exists(full))
                return Path.TrimEndingDirectorySeparator(full);
            return File.Exists(full) ? Path.GetDirectoryName(full) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static GitInfo? ResolveUncached(string directory)
    {
        try
        {
            for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            {
                var dotGit = Path.Combine(current.FullName, ".git");
                if (Directory.Exists(dotGit))
                    return Describe(current.FullName, dotGit);
                if (File.Exists(dotGit) && ReadGitDirLink(dotGit, current.FullName) is { } linked)
                    return Describe(current.FullName, linked);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    private static string? ReadGitDirLink(string dotGitFile, string root)
    {
        var line = File.ReadLines(dotGitFile).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.Ordinal));
        if (line is null)
            return null;
        var target = Path.GetFullPath(Path.Combine(root, line["gitdir:".Length..].Trim()));
        return Directory.Exists(target) ? target : null;
    }

    private static GitInfo Describe(string root, string gitDir)
    {
        var commonDir = ReadCommonDir(gitDir);
        var isWorktree = !string.Equals(Normalize(commonDir), Normalize(gitDir), StringComparison.OrdinalIgnoreCase);
        var mainRoot = !isWorktree ? root
            : Path.GetFileName(Normalize(commonDir)) == ".git" ? Path.GetDirectoryName(Normalize(commonDir))! : Normalize(commonDir);

        var (branch, commit) = ReadHead(gitDir, commonDir);
        return new GitInfo(
            RepoName(commonDir, mainRoot),
            mainRoot,
            root,
            isWorktree ? Path.GetFileName(root) : null,
            branch,
            commit is { Length: > 7 } ? commit[..7] : commit);
    }

    private static string ReadCommonDir(string gitDir)
    {
        var file = Path.Combine(gitDir, "commondir");
        return File.Exists(file) ? Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(file).Trim())) : gitDir;
    }

    private static (string? Branch, string? Commit) ReadHead(string gitDir, string commonDir)
    {
        var headFile = Path.Combine(gitDir, "HEAD");
        if (!File.Exists(headFile))
            return (null, null);

        var head = File.ReadAllText(headFile).Trim();
        if (!head.StartsWith("ref:", StringComparison.Ordinal))
            return (null, head);

        var reference = head["ref:".Length..].Trim();
        var branch = reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference["refs/heads/".Length..] : reference;
        return (branch, ReadRef(gitDir, reference) ?? ReadRef(commonDir, reference) ?? ReadPackedRef(commonDir, reference));
    }

    private static string? ReadRef(string dir, string reference)
    {
        var file = Path.Combine(dir, reference.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
    }

    private static string? ReadPackedRef(string commonDir, string reference)
    {
        var file = Path.Combine(commonDir, "packed-refs");
        if (!File.Exists(file))
            return null;
        return File.ReadLines(file)
            .Select(line => line.Split(' ', 2))
            .FirstOrDefault(parts => parts.Length == 2 && parts[1] == reference)?[0];
    }

    private static string RepoName(string commonDir, string mainRoot)
    {
        var config = Path.Combine(commonDir, "config");
        if (File.Exists(config))
        {
            var inOrigin = false;
            foreach (var raw in File.ReadLines(config))
            {
                var line = raw.Trim();
                if (line.StartsWith('['))
                    inOrigin = line == "[remote \"origin\"]";
                else if (inOrigin && line.StartsWith("url", StringComparison.Ordinal) && line.Contains('='))
                {
                    var url = line[(line.IndexOf('=') + 1)..].Trim().TrimEnd('/');
                    var name = url[(url.LastIndexOfAny(['/', ':', '\\']) + 1)..];
                    if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                        name = name[..^4];
                    if (name.Length > 0)
                        return name;
                }
            }
        }
        var folder = Path.GetFileName(mainRoot);
        return folder.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? folder[..^4] : folder;
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
