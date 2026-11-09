namespace TestControllerGrpc.Menus;

/// <summary>Which tree the node was right-clicked in.</summary>
public enum NodeMenuTree { WatchList, Library }

/// <summary>Menu sections; a separator is drawn wherever the group changes.</summary>
public enum NodeMenuGroup { Run, Context, Add, Edit, Copy, Move, State, Delete }

/// <summary>One menu entry. Hosts map <see cref="Id"/> to their own command and icon.</summary>
public sealed record NodeMenuItem(string Id, string Label, NodeMenuGroup Group)
{
    public bool IsEnabled { get; init; } = true;

    /// <summary>Why the item is disabled, or an explanation shown as its tooltip.</summary>
    public string? ToolTip { get; init; }

    /// <summary>Non-null for a checkable item.</summary>
    public bool? IsChecked { get; init; }

    public IReadOnlyList<NodeMenuItem> Children { get; init; } = [];
}

/// <summary>Everything the menu rules need to know about the clicked node. Hosts fill it in.</summary>
public sealed record NodeMenuContext
{
    public required string NodeKind { get; init; }
    public NodeMenuTree Tree { get; init; } = NodeMenuTree.WatchList;

    /// <summary>The node is a pipeline's Ref, or a read-only template node shown under one.</summary>
    public bool InTemplateCall { get; init; }

    /// <summary>The node is the pipeline's Ref itself (not a mirrored child).</summary>
    public bool IsTemplateCallRoot { get; init; }

    public string? CalledTemplateId { get; init; }
    public bool CalledTemplateExists { get; init; }

    public bool IsSkippable { get; init; }
    public bool IsSkipped { get; init; }

    /// <summary>WatchItem only: included in Trigger All.</summary>
    public bool IsEnabled { get; init; }

    public string? ExecutionType { get; init; }
    public bool CanMoveUp { get; init; }
    public bool CanMoveDown { get; init; }

    /// <summary>Why the node cannot run right now; null when it can.</summary>
    public string? RunBlockedReason { get; init; }

    public bool CanEnablePipelines { get; init; }
    public bool CanDisablePipelines { get; init; }
    public bool CanToggleThisPipeline { get; init; }
    public bool CanAuthorPipelines { get; init; }

    public bool PipelineBuilderEnabled { get; init; }
    public bool GlobalVariablesEnabled { get; init; }

    /// <summary>Why the user cannot edit this node now (rule 4/5); null when edits are allowed.</summary>
    public string? EditBlockedReason { get; init; }

    /// <summary>WatchItem only: why the pipeline itself cannot be deleted or moved (needs Pipeline_Author).</summary>
    public string? StructuralEditBlockedReason { get; init; }

    /// <summary>Why the user cannot change Library templates (Save group as template); null when allowed.</summary>
    public string? TemplateEditBlockedReason { get; init; }

    /// <summary>A node has been copied and can be pasted.</summary>
    public bool HasClipboard { get; init; }

    /// <summary>Failed actions in the last session of this pipeline; 0 hides Retry.</summary>
    public int RetryFailedCount { get; init; }
    public bool CanRetry { get; init; }

    /// <summary>Library only: the template this node belongs to, and its chosen pipeline context.</summary>
    public string? OwningTemplateId { get; init; }
    public string? TemplateContext { get; init; }
}
