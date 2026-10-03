namespace TestControllerGrpc.Core.Preflight;

/// <summary>Result of asking whether a path is there.</summary>
public enum PathAccess
{
    Exists,
    Missing,

    /// <summary>
    /// The identity doing the checking cannot see the path. NOT the same as missing: reporting it
    /// as missing turns "I am not allowed to look" into "it is not there" and blocks a good run.
    /// </summary>
    Denied,
}

/// <summary>
/// The file-system questions pre-flight asks. An interface so the checks are testable without
/// creating real UNC shares, and so a hung network path can be bounded in one place.
/// </summary>
public interface IPreflightFileSystem
{
    PathAccess CheckFile(string path);
    PathAccess CheckDirectory(string path);

    /// <summary>Free space on the volume holding <paramref name="path"/>, or null when unknown.</summary>
    double? FreeSpaceGb(string path);

    /// <summary>Who is doing the checking. Named in an access-denied warning so it is actionable.</summary>
    string Identity { get; }
}

/// <inheritdoc />
public sealed class PreflightFileSystem : IPreflightFileSystem
{
    public string Identity { get; } = $@"{Environment.UserDomainName}\{Environment.UserName}";

    public PathAccess CheckFile(string path)
    {
        try
        {
            if (File.Exists(path)) return PathAccess.Exists;
        }
        catch (UnauthorizedAccessException) { return PathAccess.Denied; }
        catch (IOException) { return PathAccess.Denied; }

        return Classify(Path.GetDirectoryName(path));
    }

    public PathAccess CheckDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) return PathAccess.Exists;
        }
        catch (UnauthorizedAccessException) { return PathAccess.Denied; }
        catch (IOException) { return PathAccess.Denied; }

        return Classify(Path.GetDirectoryName(path?.TrimEnd('\\', '/')));
    }

    /// <summary>
    /// File.Exists and Directory.Exists swallow every exception, so a share this identity cannot
    /// authenticate to is indistinguishable from one that is absent. Enumerating the PARENT asks a
    /// question that actually throws, which separates the two.
    /// </summary>
    private static PathAccess Classify(string? parent)
    {
        if (string.IsNullOrEmpty(parent)) return PathAccess.Missing;

        try
        {
            using var e = Directory.EnumerateFileSystemEntries(parent).GetEnumerator();
            e.MoveNext();
            return PathAccess.Missing;
        }
        catch (DirectoryNotFoundException) { return PathAccess.Missing; }
        catch (UnauthorizedAccessException) { return PathAccess.Denied; }
        catch (IOException) { return PathAccess.Denied; }
        catch { return PathAccess.Missing; }
    }

    public double? FreeSpaceGb(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return null;
            // UNC roots have no DriveInfo; the share's free space is the agent's business anyway.
            if (root.StartsWith(@"\\", StringComparison.Ordinal)) return null;

            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace / 1024d / 1024d / 1024d : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// What pre-flight knows about one agent. Composed by the host from the agent roster, the telemetry
/// cache, the lock manager and the maintenance store - pre-flight itself touches no machine.
/// </summary>
public sealed record PreflightAgentFacts
{
    public required string AgentName { get; init; }
    public bool IsRegistered { get; init; }
    public bool IsOnline { get; init; }
    public bool IsBusy { get; init; }
    public string? MaintenanceState { get; init; }
    public string? LockedBy { get; init; }
    public double? DiskFreeGb { get; init; }
    public bool RebootRequired { get; init; }

    public static PreflightAgentFacts Unknown(string name) => new()
    {
        AgentName = name,
        IsRegistered = false,
    };
}
