using DevPortal.Discovery;

namespace DevPortal.Tests;

public sealed class CommandLineTests
{
    [Theory]
    [InlineData(@"dotnet run --project C:\src\Shop\Shop.Api", @"C:\work", @"C:\src\Shop\Shop.Api")]
    [InlineData(@"""C:\Program Files\nodejs\node.exe"" node_modules\vite\bin\vite.js --port 5173", @"C:\src\web", @"C:\src\web\node_modules\vite\bin\vite.js")]
    [InlineData(@"python -m uvicorn app.main:app --app-dir=..\api --port 8000", @"C:\src\svc\tools", @"C:\src\svc\api")]
    [InlineData(@"php -S localhost:8080 -t public", @"C:\src\site", @"C:\src\site\public")]
    public void First_candidate_path_points_at_the_project(string commandLine, string workingDirectory, string expected)
    {
        Assert.Equal(expected, CommandLine.CandidatePaths(commandLine, workingDirectory)[0]);
    }
}
