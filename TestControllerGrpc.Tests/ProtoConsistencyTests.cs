using System.Security.Cryptography;

namespace TestControllerGrpc.Tests;

/// <summary>
/// CORE-001: Verifies all proto files across projects are byte-identical
/// to the canonical TestControllerGrpc.Core proto file.
/// Proto divergence between projects causes subtle runtime failures.
/// </summary>
public class ProtoConsistencyTests
{
    /// <summary>
    /// Path to the solution root (navigate up from test output directory).
    /// </summary>
    private static string SolutionRoot
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            // Walk up from bin/Debug/net10.0-windows until we find the .sln
            while (dir != null && !File.Exists(Path.Combine(dir, "TestAgentSolution.sln")))
                dir = Path.GetDirectoryName(dir);
            return dir ?? throw new InvalidOperationException("Cannot find solution root");
        }
    }

    private static string CanonicalProto =>
        Path.Combine(SolutionRoot, "TestControllerGrpc.Core", "Protos", "test_agent.proto");

    public static IEnumerable<object[]> ProtoCopies =>
    [
        [Path.Combine("TestAgentGrpc", "Protos", "test_agent.proto")],
        [Path.Combine("TestControllerGrpc", "Protos", "test_agent.proto")],
        [Path.Combine("TestAgentDisplay", "Protos", "test_agent.proto")],
    ];

    [Fact]
    public void CanonicalProto_ShouldExist()
    {
        Assert.True(File.Exists(CanonicalProto),
            $"Canonical proto not found at: {CanonicalProto}");
    }

    [Theory]
    [MemberData(nameof(ProtoCopies))]
    public void ProtoCopy_ShouldMatchCanonical(string relativePath)
    {
        var copyPath = Path.Combine(SolutionRoot, relativePath);
        if (!File.Exists(copyPath))
        {
            // Skip if project doesn't exist (e.g. trimmed solution)
            return;
        }

        var canonicalHash = ComputeFileHash(CanonicalProto);
        var copyHash = ComputeFileHash(copyPath);

        Assert.True(canonicalHash == copyHash,
            $"Proto file '{relativePath}' has diverged from canonical " +
            $"'TestControllerGrpc.Core/Protos/test_agent.proto'.\n" +
            $"Canonical SHA256: {canonicalHash}\n" +
            $"Copy SHA256:      {copyHash}\n" +
            $"Run: copy /Y \"{CanonicalProto}\" \"{copyPath}\" to sync.");
    }

    private static string ComputeFileHash(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}
