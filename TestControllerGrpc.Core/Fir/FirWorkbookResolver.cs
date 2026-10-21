namespace TestControllerGrpc.Core.Fir;

/// <summary>Which copy of the test-data workbook a report was built from.</summary>
public enum FirDataSource
{
    None,

    /// <summary>The copy that travelled back with the results - by definition the one the run used.</summary>
    CopyFromRun,

    /// <summary>A copy held on the controller, accepted only after it was matched against the log.</summary>
    ControllerCopy,
}

public sealed record FirWorkbookMatch(string Path, FirDataSource Source);

public sealed record FirOptions
{
    public static FirOptions Default { get; } = new();

    /// <summary>Folders searched by workbook file name when the run did not bring its own copy.</summary>
    public IReadOnlyList<string> TestDataRoots { get; init; } = [];
}

/// <summary>
/// Finds the workbook named in the log. The logged path is the AGENT's run folder and does not exist on
/// the controller, so only the file name is reused. Nothing here reaches across the network: agent VMs
/// are reverted and routinely offline, and a report must never depend on one being up.
/// </summary>
public static class FirWorkbookResolver
{
    private const string TestDataFolder = "TestData";

    private static readonly EnumerationOptions Search = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        MaxRecursionDepth = 6,
    };

    public static FirWorkbookMatch? Resolve(string? loggedPath, string? resultsFolder, FirOptions? options)
    {
        var name = SafeFileName(loggedPath);
        if (name is null) return null;

        if (!string.IsNullOrWhiteSpace(resultsFolder) && Directory.Exists(resultsFolder))
        {
            if (FindIn(Path.Combine(resultsFolder, TestDataFolder), name) is { } beside)
                return new FirWorkbookMatch(beside, FirDataSource.CopyFromRun);

            var flat = Path.Combine(resultsFolder, name);
            if (File.Exists(flat)) return new FirWorkbookMatch(flat, FirDataSource.CopyFromRun);
        }

        foreach (var root in options?.TestDataRoots ?? [])
            if (FindIn(root, name) is { } onController)
                return new FirWorkbookMatch(onController, FirDataSource.ControllerCopy);

        return null;
    }

    private static string? FindIn(string folder, string fileName)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;

        try
        {
            return Directory.EnumerateFiles(folder, fileName, Search).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The logged path comes from a foreign machine, so it may not be a valid local path at all.</summary>
    private static string? SafeFileName(string? loggedPath)
    {
        if (string.IsNullOrWhiteSpace(loggedPath)) return null;

        try
        {
            var name = Path.GetFileName(loggedPath.Trim());
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
