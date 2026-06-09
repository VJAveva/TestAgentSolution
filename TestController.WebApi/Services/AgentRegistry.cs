using System.Collections.Concurrent;

namespace TestController.WebApi.Services;

/// <summary>
/// In-memory registry of known agents (name ? address).
/// Populated from appsettings on startup and from POST /api/agents/register at runtime.
/// </summary>
public sealed class AgentRegistry
{
    private readonly ConcurrentDictionary<string, AgentEntry> _agents = new(StringComparer.OrdinalIgnoreCase);

    public AgentRegistry(IConfiguration config)
    {
        // Seed from appsettings "Agents" array
        var section = config.GetSection("Agents");
        foreach (var child in section.GetChildren())
        {
            var name = child["Name"];
            var address = child["Address"];
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(address))
            {
                var entry = new AgentEntry(name, address);
                // Auto-resolve hostname from the address URL
                if (Uri.TryCreate(address, UriKind.Absolute, out var uri))
                    entry.Hostname = uri.Host;
                _agents[name] = entry;
            }
        }
    }

    public bool TryGet(string name, out AgentEntry entry) => _agents.TryGetValue(name, out entry!);

    public IReadOnlyList<AgentEntry> GetAll() => _agents.Values.ToList();

    public void Register(string name, string address)
        => _agents[name] = new AgentEntry(name, address);

    public bool Unregister(string name)
        => _agents.TryRemove(name, out _);

    public void UpdateStatus(string name, string status, string? detail = null)
    {
        if (_agents.TryGetValue(name, out var entry))
        {
            entry.Status = status;
            entry.LastStatusDetail = detail;
            entry.LastCheckedUtc = DateTime.UtcNow;
        }
    }
}

public sealed class AgentEntry
{
    public AgentEntry(string name, string address)
    {
        Name = name;
        Address = address;
    }

    public string Name { get; }
    public string Address { get; }
    public string? Hostname { get; set; }
    public string Status { get; set; } = "Unknown";
    public string? LastStatusDetail { get; set; }
    public DateTime? LastCheckedUtc { get; set; }
}
