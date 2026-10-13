namespace DevPortal.Discovery;

internal static class ServerKindDetector
{
    public static ServerKind Detect(ProcessDetails process)
    {
        var name = process.Name.ToLowerInvariant();
        var commandLine = process.CommandLine?.ToLowerInvariant() ?? "";
        var exe = process.ExecutablePath?.ToLowerInvariant() ?? "";

        if (name is "dotnet" or "iisexpress" || IsDotnetBuildOutput(exe) || commandLine.Contains("kestrel"))
            return ServerKind.Dotnet;
        if (name is "node" or "bun" or "deno")
            return ServerKind.Node;
        if (name.StartsWith("python") || name is "uvicorn" or "gunicorn" or "hypercorn" or "flask")
            return ServerKind.Python;
        if (name.StartsWith("php") || name == "frankenphp")
            return ServerKind.Php;
        if (name is "java" or "javaw")
            return ServerKind.Java;
        return ServerKind.Unknown;
    }

    private static bool IsDotnetBuildOutput(string exe) =>
        exe.Contains(@"\bin\debug\net") || exe.Contains(@"\bin\release\net") || exe.Contains(@"\publish\");
}
