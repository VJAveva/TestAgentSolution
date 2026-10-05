using System.Text.RegularExpressions;

namespace TestControllerGrpc.Tests.Maintenance;

/// <summary>
/// RevertRcloudMachine.bat used to `echo [INFO] Args : %*`, where args 3 and 4 are the vCloud user and
/// password. The agent relays stdout to the controller, so every revert wrote the live credential into
/// app_*.log in plaintext - 45 such lines existed across four days of logs when this was found.
/// Pure file analysis, so it cannot flake.
/// </summary>
public class RevertScriptSecretTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TestAgentSolution.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }

    private static string ScriptDir() => Path.Combine(RepoRoot(), "Utilites", "RevertAgents");

    public static TheoryData<string> BatchFiles()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.EnumerateFiles(ScriptDir(), "*.bat", SearchOption.AllDirectories))
            data.Add(Path.GetFileName(f));
        return data;
    }

    [Theory]
    [MemberData(nameof(BatchFiles))]
    public void Launcher_Should_NeverEchoAllArguments_When_ArgumentsCarryCredentials(string fileName)
    {
        var text = File.ReadAllText(Path.Combine(ScriptDir(), fileName));

        // %* expands to every argument, and the credential positions travel with it.
        var offenders = Regex.Matches(text, @"(?im)^\s*echo\b[^\r\n]*%\*")
            .Select(m => m.Value.Trim())
            .ToList();

        Assert.True(offenders.Count == 0,
            $"{fileName} echoes all arguments, which writes the vCloud password into the controller log:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Theory]
    [MemberData(nameof(BatchFiles))]
    public void Launcher_Should_NeverEchoACredentialPosition_When_Logging(string fileName)
    {
        var text = File.ReadAllText(Path.Combine(ScriptDir(), fileName));

        // Positions 3 and 4 are <user> <password> in the revert contract.
        var offenders = Regex.Matches(text, @"(?im)^\s*echo\b[^\r\n]*%[34](?![0-9])")
            .Select(m => m.Value.Trim())
            .ToList();

        Assert.True(offenders.Count == 0,
            $"{fileName} echoes a credential argument position:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Launcher_Should_StillForwardArguments_When_InvokingTheWorker()
    {
        // Redaction must not have broken the call itself: the worker still needs every argument.
        var text = File.ReadAllText(Path.Combine(ScriptDir(), "RevertRcloudMachine.bat"));

        Assert.Matches(@"powershell\.exe[^\r\n]*-File[^\r\n]*%\*", text);
    }

    [Fact]
    public void PowerShellScripts_Should_NeverLogThePasswordVariable()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(ScriptDir(), "*.ps1", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var hits = Regex.Matches(text, @"(?im)^\s*(Log|Info|Write-Host|Write-Output)\b[^\r\n]*\$Password")
                .Select(m => $"{Path.GetFileName(file)}: {m.Value.Trim()}");
            offenders.AddRange(hits);
        }

        Assert.True(offenders.Count == 0,
            "A revert script logs the password variable:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }
}
