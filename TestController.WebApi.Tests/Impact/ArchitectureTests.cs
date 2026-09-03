using System.Reflection;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Text;

namespace TestController.WebApi.Tests.Impact;

public sealed class ArchitectureTests
{
    private static readonly string[] ForbiddenAssemblies =
    [
        "PresentationFramework", "PresentationCore", "WindowsBase", "System.Windows.Forms", "Microsoft.AspNetCore.SignalR",
    ];

    [Fact]
    public void CoreAssembly_Should_NotReferenceHostFrameworks()
    {
        AssertNoForbiddenReferences(typeof(Tokenizer).Assembly);
    }

    [Fact]
    public void ImpactAssembly_Should_NotReferenceHostFrameworks()
    {
        AssertNoForbiddenReferences(typeof(ImpactTestMappingService).Assembly);
    }

    private static void AssertNoForbiddenReferences(Assembly assembly)
    {
        List<string> referenced = assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToList();

        foreach (string forbidden in ForbiddenAssemblies)
        {
            Assert.DoesNotContain(referenced, r => r.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }
}
