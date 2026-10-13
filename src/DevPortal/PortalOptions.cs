namespace DevPortal;

public sealed class PortalOptions
{
    public const string Section = "DevPortal";

    public int CacheSeconds { get; set; } = 3;
    public int ProbeTimeoutMs { get; set; } = 1500;
    public int ProbeCacheSeconds { get; set; } = 30;
    public string DockerPipe { get; set; } = "docker_engine";
    public string[] SystemProcesses { get; set; } = [];
    public int[] NonHttpPorts { get; set; } = [];
    public int[] HiddenPorts { get; set; } = [];
}
