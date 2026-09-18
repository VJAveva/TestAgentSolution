using System.IO;

namespace TestControllerGrpc.Tests.Ui;

/// <summary>
/// G-8 of the UI acceptance criteria. A WPF process that does not declare PerMonitorV2 is bitmap-scaled
/// by Windows on any monitor whose scaling differs from the one it launched on - every glyph goes soft
/// and nothing in the app can detect it. The failure is invisible to a build, to every other test, and
/// to anyone working on a single-scaling desktop.
///
/// Pure file analysis, like the rest of the Ui guards: no STA thread, no WPF instantiation.
/// </summary>
public sealed class DpiAwarenessTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TestAgentSolution.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ManifestPath() => Path.Combine(RepoRoot(), "TestControllerGrpc", "app.manifest");

    private static string CsprojPath() =>
        Path.Combine(RepoRoot(), "TestControllerGrpc", "TestControllerGrpc.csproj");

    [Fact]
    public void Manifest_Should_DeclarePerMonitorV2_When_HostRendersOnMixedDpiMonitors()
    {
        var path = ManifestPath();
        Assert.True(File.Exists(path), $"No application manifest at {path}. Without one the WPF host runs "
                                       + "bitmap-scaled on secondary monitors.");

        var manifest = File.ReadAllText(path);
        Assert.Contains("<dpiAwareness", manifest);
        Assert.Contains("PerMonitorV2", manifest);
    }

    /// <summary>
    /// A manifest on disk that the project never references is inert - the same silent-no-op shape as a
    /// .proto with no Protobuf item. The build succeeds and the app stays bitmap-scaled.
    /// </summary>
    [Fact]
    public void Csproj_Should_ReferenceTheManifest_When_ManifestExists()
    {
        var csproj = File.ReadAllText(CsprojPath());

        Assert.Contains("<ApplicationManifest>app.manifest</ApplicationManifest>", csproj);
    }

    /// <summary>
    /// Windows ignores the PerMonitorV2 element unless the app also declares Windows 10 support, so
    /// dropping the compatibility block silently reverts the app to bitmap scaling.
    /// </summary>
    [Fact]
    public void Manifest_Should_DeclareWindows10Support_When_PerMonitorV2IsRequested()
    {
        var manifest = File.ReadAllText(ManifestPath());

        Assert.Contains("{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}", manifest);
    }
}
