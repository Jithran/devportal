using System.Security.Cryptography;
using System.Text;

namespace DevPortal.Preview;

/// <summary>
/// Screenshots and favicons of local pages, one file per endpoint owner and kind. Files stay on this machine only;
/// the folder is deleted when previews are switched off.
/// </summary>
public sealed class PreviewCache(string directory, TimeProvider time)
{
    public const int MaxFiles = 50;
    public const long MaxBytes = 20 * 1024 * 1024;
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    public string Directory => directory;

    /// <summary>The cached file, also when it is past its TTL: an old preview beats none until the refresh lands.</summary>
    public string? Find(string key, string kind)
    {
        var path = PathFor(key, kind);
        return File.Exists(path) ? path : null;
    }

    public bool IsFresh(string key, string kind)
    {
        var file = new FileInfo(PathFor(key, kind));
        return file.Exists && time.GetUtcNow() - file.LastWriteTimeUtc < Ttl;
    }

    public void Store(string key, string kind, Action<string> write)
    {
        System.IO.Directory.CreateDirectory(directory);
        var path = PathFor(key, kind);
        var temp = path + ".tmp";
        write(temp);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Drops files older than <see cref="MaxAge"/>, then the oldest until the count and size caps hold.</summary>
    public void Trim()
    {
        if (!System.IO.Directory.Exists(directory))
            return;
        var now = time.GetUtcNow();
        var files = new DirectoryInfo(directory).GetFiles().OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        long bytes = 0;
        for (var i = 0; i < files.Count; i++)
        {
            bytes += files[i].Length;
            if (i >= MaxFiles || bytes > MaxBytes || now - files[i].LastWriteTimeUtc > MaxAge)
                TryDelete(files[i]);
        }
    }

    public void Clear()
    {
        if (System.IO.Directory.Exists(directory))
            System.IO.Directory.Delete(directory, recursive: true);
    }

    private string PathFor(string key, string kind) =>
        Path.Combine(directory, $"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32]}.{kind}");

    private static void TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch (IOException)
        {
            // Being served right now; the next trim gets it.
        }
    }
}
