using System.Text.Json.Serialization;

namespace TestControllerGrpc.Models;

/// <summary>
/// Skip state and free-text comment, carried by every node a user can right-click:
/// WatchItem, Event, ActionGroup and Action.
/// </summary>
/// <remarks>
/// <see cref="Skip"/> is set ONLY on the node the user clicked. The cascade to children is COMPUTED at
/// traversal time by <see cref="SkipEvaluator"/>, never written to the children.
///
/// Persisting the cascade would mean that unskipping a parent leaves every descendant still flagged — a
/// stale-state bug that surfaces the first time someone changes their mind, and one the file itself would
/// then carry forever.
///
/// The cascade is evaluated top-down rather than through a <c>Parent</c> back-reference, because this tree is
/// JSON-serialized for the WebClient and deep-cloned by XML round-trip; back-references would introduce cycles
/// and a second source of truth to keep in sync on every move/copy.
/// </remarks>
public interface ISkippableNode
{
    /// <summary>Explicitly skipped by a user. Does NOT imply anything about descendants.</summary>
    bool Skip { get; set; }

    string? SkipReason { get; set; }

    /// <summary>Free text about the node. Independent of <see cref="Skip"/> — a node can be documented and still run.</summary>
    string? Comment { get; set; }

    DateTimeOffset? SkippedAtUtc { get; set; }

    /// <summary>Server-assigned from the caller's identity; never trusted from a client payload.</summary>
    string? SkippedBy { get; set; }
}

/// <summary>Why a node is not going to run.</summary>
public enum SkipOrigin
{
    /// <summary>Not skipped.</summary>
    None = 0,
    /// <summary>The user skipped this exact node.</summary>
    Explicit = 1,
    /// <summary>An ancestor is skipped, so this node cannot run either.</summary>
    Inherited = 2,
}

/// <summary>One node's resolved skip state, with the ancestor responsible when inherited.</summary>
public sealed record SkipState(SkipOrigin Origin, string? Reason, string? SkippedByNode)
{
    public static readonly SkipState NotSkipped = new(SkipOrigin.None, null, null);

    public bool IsSkipped => Origin != SkipOrigin.None;
}
