using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TestControllerGrpc.Configuration;

/// <summary>
/// Generic writable options that serializes changes back to appsettings.json.
/// Used by RbacModeTransitionService to flip RBAC:Enabled live.
/// Per 05_Default_Mode_Design.md §2.1.
/// </summary>
public interface IWritableOptions<out T> where T : class, new()
{
    T Value { get; }
    void Update(Action<T> applyChanges);
}

public sealed class WritableOptions<T> : IWritableOptions<T> where T : class, new()
{
    private readonly IOptionsMonitor<T> _options;
    private readonly string _section;
    private readonly string _filePath;
    private readonly object _lock = new();

    public WritableOptions(IOptionsMonitor<T> options, string section, string filePath)
    {
        _options = options;
        _section = section;
        _filePath = filePath;
    }

    public T Value => _options.CurrentValue;

    public void Update(Action<T> applyChanges)
    {
        lock (_lock)
        {
            var json = File.Exists(_filePath)
                ? File.ReadAllText(_filePath)
                : "{}";

            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
                ?? new Dictionary<string, JsonElement>();

            var sectionValue = new T();
            if (doc.TryGetValue(_section, out var existing))
            {
                sectionValue = JsonSerializer.Deserialize<T>(existing.GetRawText()) ?? new T();
            }

            applyChanges(sectionValue);

            doc[_section] = JsonSerializer.Deserialize<JsonElement>(
                JsonSerializer.Serialize(sectionValue));

            var output = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, output);
        }
    }
}
