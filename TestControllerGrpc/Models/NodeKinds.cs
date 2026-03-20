namespace TestControllerGrpc.Models;

/// <summary>
/// Centralized constants for tree node kind identifiers.
/// Eliminates magic strings across ViewModels and Views.
/// </summary>
public static class NodeKinds
{
    public const string WatchList = "WatchList";
    public const string WatchItem = "WatchItem";
    public const string Event = "Event";
    public const string ActionGroup = "ActionGroup";
    public const string Action = "Action";
    public const string Initialize = "Initialize";
    public const string Ref = "Ref";
    public const string Template = "Template";
    public const string TemplateList = "TemplateList";
}
