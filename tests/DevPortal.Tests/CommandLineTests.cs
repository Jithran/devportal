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

    [Theory]
    [InlineData(@"app.exe --ConnectionStrings:Db ""Server=.;User Id=sa;Password=S3cr3t!;""", @"app.exe --ConnectionStrings:Db ""Server=.;User Id=sa;Password=***;""")]
    [InlineData(@"cmd /c set DB_PWD=hunter2&& set API_TOKEN=abc123&& dotnet run", @"cmd /c set DB_PWD=***&& set API_TOKEN=***&& dotnet run")]
    [InlineData(@"node server.js --api-key k-123 --port 3000", @"node server.js --api-key *** --port 3000")]
    [InlineData(@"curl -H ""Authorization: Bearer eyJhbGciOi.payload.sig"" x", @"curl -H ""Authorization: *** ***"" x")]
    [InlineData(@"worker --db mongodb://admin:pa55@localhost:27017/app --client_secret=xyz", @"worker --db mongodb://admin:***@localhost:27017/app --client_secret=***")]
    [InlineData(@"""C:\src\Shop\bin\Debug\net10.0\Shop.exe"" --urls http://localhost:5000", @"""C:\src\Shop\bin\Debug\net10.0\Shop.exe"" --urls http://localhost:5000")]
    public void Secrets_in_command_lines_are_masked(string commandLine, string expected)
    {
        Assert.Equal(expected, CommandLine.Redact(commandLine));
    }
}
