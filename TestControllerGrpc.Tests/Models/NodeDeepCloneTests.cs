using System.Reflection;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Tests.Models;

/// <summary>
/// Guards <see cref="IActionNode.DeepClone"/> against the field-dropping bug class: a hand-written
/// copy silently loses every property nobody remembers to add. <c>Skip</c> was lost that way (a
/// skipped action ran), then <c>InitializeConfig.Profile</c> (the profiles layer never applied).
///
/// The node types are discovered by reflection rather than listed, so a NEW node type or a NEW
/// property is covered the moment it is declared. An unhandled property type fails the test
/// instead of being skipped — silence is what let the bug through twice.
/// </summary>
public class NodeDeepCloneTests
{
    public static TheoryData<Type> NodeTypes()
    {
        var data = new TheoryData<Type>();
        foreach (var t in DiscoverNodeTypes()) data.Add(t);
        return data;
    }

    private static List<Type> DiscoverNodeTypes() =>
        typeof(IActionNode).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IActionNode).IsAssignableFrom(t))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void Discovery_Should_FindEveryKnownNodeType_When_AssemblyScanned()
    {
        var found = DiscoverNodeTypes().Select(t => t.Name).ToList();

        Assert.Contains(nameof(ActionConfig), found);
        Assert.Contains(nameof(ActionGroupConfig), found);
        Assert.Contains(nameof(InitializeConfig), found);
        Assert.Contains(nameof(RefConfig), found);
    }

    [Theory]
    [MemberData(nameof(NodeTypes))]
    public void DeepClone_Should_CopyEveryProperty_When_AllSetToNonDefault(Type nodeType)
    {
        var original = (IActionNode)Activator.CreateInstance(nodeType)!;

        var written = new List<string>();
        foreach (var prop in Settable(nodeType))
        {
            prop.SetValue(original, NonDefaultValue(prop, nodeType));
            written.Add(prop.Name);
        }

        Assert.NotEmpty(written);

        var clone = original.DeepClone();

        Assert.NotSame(original, clone);
        Assert.IsType(nodeType, clone);

        foreach (var prop in Readable(nodeType))
        {
            var mine = prop.GetValue(original);
            var theirs = prop.GetValue(clone);

            if (mine is List<IActionNode> originalChildren)
            {
                var clonedChildren = Assert.IsType<List<IActionNode>>(theirs);
                AssertChildrenDeepCopied(nodeType, prop.Name, originalChildren, clonedChildren);
                continue;
            }

            Assert.True(
                Equals(mine, theirs),
                $"{nodeType.Name}.{prop.Name} was NOT copied by DeepClone(): expected '{mine}' but the clone has '{theirs}'.");
        }
    }

    /// <summary>
    /// NodeId must survive: progress events are matched back to tree nodes by it, so a snapshot
    /// with a fresh id reports against nothing. This is the one property that must NOT be
    /// regenerated — the opposite rule to <see cref="ActionConfig.Clone"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(NodeTypes))]
    public void DeepClone_Should_PreserveNodeId_When_Cloned(Type nodeType)
    {
        var original = (IActionNode)Activator.CreateInstance(nodeType)!;
        original.NodeId = "stable-node-id";

        Assert.Equal("stable-node-id", original.DeepClone().NodeId);
    }

    [Fact]
    public void DeepClone_Should_IsolateNestedGroups_When_OriginalMutatedAfterwards()
    {
        var leaf = new ActionConfig { Command = "original-leaf" };
        var inner = new ActionGroupConfig { Tag = "inner", Children = [leaf] };
        var outer = new ActionGroupConfig { Tag = "outer", Children = [inner] };

        var clone = (ActionGroupConfig)outer.DeepClone();

        leaf.Command = "mutated-leaf";
        inner.Children.Add(new ActionConfig { Command = "late-arrival" });

        var clonedInner = Assert.IsType<ActionGroupConfig>(clone.Children[0]);
        Assert.Single(clonedInner.Children);
        Assert.Equal("original-leaf", Assert.IsType<ActionConfig>(clonedInner.Children[0]).Command);
    }

    [Fact]
    public void DeepClone_Should_CopyProfile_When_InitializeDeclaresOne()
    {
        var init = new InitializeConfig { Tag = "Init", ParameterFile = @"C:\p\pipeline-config.json", Profile = "Sanity" };

        Assert.Equal("Sanity", Assert.IsType<InitializeConfig>(init.DeepClone()).Profile);
    }

    private static void AssertChildrenDeepCopied(
        Type nodeType, string propName, List<IActionNode> original, List<IActionNode> clone)
    {
        Assert.False(
            ReferenceEquals(original, clone),
            $"{nodeType.Name}.{propName} is the SAME list instance; adding to the original would mutate a running snapshot.");

        Assert.Equal(original.Count, clone.Count);

        for (var i = 0; i < original.Count; i++)
        {
            Assert.False(
                ReferenceEquals(original[i], clone[i]),
                $"{nodeType.Name}.{propName}[{i}] is the SAME node instance; a hot-reload edit would reach the running pipeline.");
            Assert.Equal(original[i].NodeId, clone[i].NodeId);
        }
    }

    private static IEnumerable<PropertyInfo> Settable(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetSetMethod() is not null);

    private static IEnumerable<PropertyInfo> Readable(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead);

    /// <summary>
    /// A value guaranteed to differ from the declared default. Throws on an unrecognised type so a
    /// newly introduced property kind fails loudly rather than going unchecked.
    /// </summary>
    private static object NonDefaultValue(PropertyInfo prop, Type owner)
    {
        var t = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

        if (t == typeof(string)) return $"probe-{owner.Name}-{prop.Name}";
        if (t == typeof(bool)) return true;
        if (t == typeof(int)) return 4242;
        if (t == typeof(long)) return 4242L;
        if (t == typeof(DateTimeOffset)) return new DateTimeOffset(2031, 5, 6, 7, 8, 9, TimeSpan.Zero);
        if (t == typeof(DateTime)) return new DateTime(2031, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        if (t.IsEnum) return Enum.GetValues(t).GetValue(Enum.GetValues(t).Length - 1)!;
        if (t == typeof(List<IActionNode>))
            return new List<IActionNode> { new ActionConfig { NodeId = "child-id", Command = "child-probe" } };

        throw new Xunit.Sdk.XunitException(
            $"{owner.Name}.{prop.Name} has type {t.Name}, which this test does not know how to probe. "
            + "Add a case to NonDefaultValue so the property is actually verified — leaving it unprobed "
            + "is how dropped fields reach production.");
    }
}
