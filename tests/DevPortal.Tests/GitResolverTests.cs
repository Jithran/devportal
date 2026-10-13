using DevPortal.Discovery;

namespace DevPortal.Tests;

public sealed class GitResolverTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("devportal-").FullName;
    private readonly GitResolver resolver = new(TimeProvider.System);

    [Fact]
    public void Process_inside_a_worktree_maps_to_the_main_repo_with_worktree_and_branch()
    {
        var main = Path.Combine(root, "MarketingAutomation");
        var common = Path.Combine(main, ".git");
        var worktreeGitDir = Path.Combine(common, "worktrees", "MarketingAutomation-ma68-tabs");
        var worktree = Path.Combine(root, "MarketingAutomation-ma68-tabs");
        var binDir = Path.Combine(worktree, "Frontend", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(worktreeGitDir);
        Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(common, "config"), "[core]\n\tbare = false\n[remote \"origin\"]\n\turl = git@github.com:Jithran/MarketingAutomation.git\n");
        File.WriteAllText(Path.Combine(common, "packed-refs"), "# pack-refs\n5b91e46aa0c1d2e3f4a5b6c7d8e9f00112233445 refs/heads/local/ma-68-integration\n");
        File.WriteAllText(Path.Combine(worktreeGitDir, "HEAD"), "ref: refs/heads/local/ma-68-integration\n");
        File.WriteAllText(Path.Combine(worktreeGitDir, "commondir"), "../..\n");
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {worktreeGitDir.Replace('\\', '/')}\n");
        var executable = Path.Combine(binDir, "Frontend.exe");
        File.WriteAllBytes(executable, []);

        var info = resolver.Resolve(executable);

        Assert.Equal(new GitInfo("MarketingAutomation", main, worktree, "MarketingAutomation-ma68-tabs", "local/ma-68-integration", "5b91e46"), info);
    }

    [Theory]
    [InlineData("C:")]
    [InlineData(@"relative\path")]
    public void Paths_that_are_not_fully_qualified_never_resolve_against_our_own_working_directory(string path)
    {
        Assert.Null(resolver.Resolve(path));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
