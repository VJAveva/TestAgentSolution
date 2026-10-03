namespace TestControllerGrpc.ViewModels;

/// <summary>
/// Decides which pipeline a Templates-library node previews against, without asking.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="MainViewModel"/> because the decision is pure - it needs the
/// candidate pipelines and which of them are running, nothing else. Inside the view model it would
/// be reachable only through a 25-parameter constructor.
/// </remarks>
public static class TemplateContextResolver
{
    /// <summary>
    /// A running pipeline wins: it is the one whose values are real right now. Failing that, a
    /// single candidate is not a choice. Anything else is genuinely ambiguous and must be asked.
    /// </summary>
    public static (string? Pipeline, TemplateContextSource Source) Decide(
        IReadOnlyList<string> candidates,
        Func<string, bool> isRunning)
    {
        if (candidates.Count == 0) return (null, TemplateContextSource.Manual);

        foreach (var candidate in candidates)
            if (isRunning(candidate)) return (candidate, TemplateContextSource.Running);

        if (candidates.Count == 1) return (candidates[0], TemplateContextSource.Auto);

        return (null, TemplateContextSource.Manual);
    }
}
