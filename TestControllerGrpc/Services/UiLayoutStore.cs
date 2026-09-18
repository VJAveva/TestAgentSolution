using System.IO;
using System.Text.Json;

namespace TestControllerGrpc.Services;

/// <summary>Pane sizes and dock state for one display configuration.</summary>
public sealed class UiLayoutSnapshot
{
    // Star units, so they are resolution-independent within a display configuration.
    public double TreeWidth { get; set; }
    public double PropertiesWidth { get; set; }
    public double AgentWidth { get; set; }
    public double LogHeight { get; set; }

    public bool TreePinned { get; set; } = true;
    public bool AgentPinned { get; set; } = true;
    public bool LogPinned { get; set; } = true;
    public bool LogCollapsed { get; set; }

    /// <summary>
    /// A star width of 0 (or NaN, or something absurd) would render a pane invisible with no way to
    /// drag it back. A restored layout is only worth applying if every size is plausible.
    /// </summary>
    public bool IsUsable()
    {
        double[] sizes = [TreeWidth, PropertiesWidth, AgentWidth, LogHeight];
        return sizes.All(v => double.IsFinite(v) && v >= 0.2 && v <= 50);
    }
}

public interface IUiLayoutStore
{
    UiLayoutSnapshot? Load(string displayKey);
    bool Save(string displayKey, UiLayoutSnapshot snapshot);
}

/// <summary>
/// Per-user pane layout, keyed by display configuration so docking a laptop does not drag a 4K layout
/// onto a 1080p screen. Every operation is best-effort: a preferences file must never be able to stop
/// the app starting or exiting.
/// </summary>
public sealed class UiLayoutStore : IUiLayoutStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;

    public UiLayoutStore(string? directory = null)
    {
        directory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TestAgentSolution");

        _path = Path.Combine(directory, "ui-layout.json");
    }

    public UiLayoutSnapshot? Load(string displayKey)
    {
        try
        {
            if (!File.Exists(_path)) return null;

            var all = JsonSerializer.Deserialize<Dictionary<string, UiLayoutSnapshot>>(File.ReadAllText(_path));
            if (all is null || !all.TryGetValue(displayKey, out var snapshot)) return null;

            return snapshot.IsUsable() ? snapshot : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool Save(string displayKey, UiLayoutSnapshot snapshot)
    {
        if (!snapshot.IsUsable()) return false;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            Dictionary<string, UiLayoutSnapshot> all;
            try
            {
                all = File.Exists(_path)
                    ? JsonSerializer.Deserialize<Dictionary<string, UiLayoutSnapshot>>(File.ReadAllText(_path)) ?? []
                    : [];
            }
            catch (JsonException)
            {
                // A corrupt file must not permanently block saving; start a fresh one.
                all = [];
            }

            all[displayKey] = snapshot;
            File.WriteAllText(_path, JsonSerializer.Serialize(all, Json));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
