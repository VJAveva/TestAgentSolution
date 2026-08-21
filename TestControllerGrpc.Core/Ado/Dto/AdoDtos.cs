using System.Text.Json.Serialization;

namespace TestControllerGrpc.Ado.Dto;

// =============================================================================
// Raw Azure DevOps REST API v7.1 response shapes. Model ONLY fields we actually
// consume — per Copilot-Prompts-AdoIntegration.md Phase C1, these should be
// validated against captured JSON fixtures (tests/Fixtures/Ado/) before trusting
// them against production. Field names below follow the documented ADO camelCase
// convention except work item "fields", which ADO returns as a PascalCase dict.
// =============================================================================

public sealed class AdoListResponse<T>
{
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("value")] public List<T> Value { get; set; } = [];
}

public sealed class AdoBuildDto
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("buildNumber")] public string BuildNumber { get; set; } = "";
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("result")] public string? Result { get; set; }
    [JsonPropertyName("startTime")] public DateTimeOffset? StartTime { get; set; }
    [JsonPropertyName("finishTime")] public DateTimeOffset? FinishTime { get; set; }
    [JsonPropertyName("sourceBranch")] public string? SourceBranch { get; set; }
    [JsonPropertyName("sourceVersion")] public string? SourceVersion { get; set; }
    [JsonPropertyName("definition")] public AdoBuildDefinitionDto? Definition { get; set; }
    [JsonPropertyName("repository")] public AdoBuildRepositoryDto? Repository { get; set; }
}

public sealed class AdoBuildDefinitionDto
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class AdoBuildRepositoryDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

/// <summary>One entry from GET builds/{id}/changes — a commit/PR since the previous build (ADR-05).</summary>
public sealed class AdoBuildChangeDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; } // "commit" | "pullRequest" etc.
    [JsonPropertyName("author")] public AdoIdentityDto? Author { get; set; }
    [JsonPropertyName("timestamp")] public DateTimeOffset? Timestamp { get; set; }
    [JsonPropertyName("location")] public string? Location { get; set; }
    [JsonPropertyName("displayUri")] public string? DisplayUri { get; set; } // web link to the commit/PR
}

public sealed class AdoIdentityDto
{
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("uniqueName")] public string? UniqueName { get; set; }
}

/// <summary>One entry from GET repositories/{repo}/commits/{id}/changes — changed files for a commit.</summary>
public sealed class AdoCommitChangesDto
{
    [JsonPropertyName("changes")] public List<AdoCommitFileChangeDto> Changes { get; set; } = [];
}

public sealed class AdoCommitFileChangeDto
{
    [JsonPropertyName("item")] public AdoCommitFileItemDto? Item { get; set; }
    [JsonPropertyName("changeType")] public string? ChangeType { get; set; } // add|edit|delete|rename
}

public sealed class AdoCommitFileItemDto
{
    [JsonPropertyName("path")] public string? Path { get; set; }
}

public sealed class AdoRepositoryDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("defaultBranch")] public string? DefaultBranch { get; set; }
}

/// <summary>A git tree item (used to find .sln files under /src for the Subsystems column).</summary>
public sealed class AdoGitItemDto
{
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("isFolder")] public bool IsFolder { get; set; }
    [JsonPropertyName("gitObjectType")] public string? GitObjectType { get; set; }
}

/// <summary>Work item as returned from the workitemsbatch endpoint.</summary>
public sealed class AdoWorkItemDto
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("fields")] public Dictionary<string, System.Text.Json.JsonElement> Fields { get; set; } = [];

    public string? Title => GetString("System.Title");
    public string? WorkItemType => GetString("System.WorkItemType");

    public DateTimeOffset? CreatedUtc =>
        Fields.TryGetValue("System.CreatedDate", out var el) && el.ValueKind == System.Text.Json.JsonValueKind.String
            && DateTimeOffset.TryParse(el.GetString(), out var dt) ? dt : null;

    private string? GetString(string key) =>
        Fields.TryGetValue(key, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.String
            ? el.GetString()
            : null;
}

/// <summary>Simplified test suite shape — the real Test Plans API is deeper; we only need id/name/plan for chip linking.</summary>
public sealed class AdoTestSuiteDto
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("plan")] public AdoTestPlanRefDto? Plan { get; set; }
}

public sealed class AdoTestPlanRefDto
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}
