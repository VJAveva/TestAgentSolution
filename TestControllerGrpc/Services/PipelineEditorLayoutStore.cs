using System.IO;
using System.Text.Json;

namespace TestControllerGrpc.Services;

/// <summary>The Pipeline Editor's layout for this Windows user: view, wrap, font size, pane widths and window size.</summary>
public sealed record PipelineEditorLayout(
    string View, bool WordWrap, double FontSize, double StructureWidth, double PropertiesWidth, double Width, double Height)
{
    public bool IsUsable() =>
        FontSize is >= 8 and <= 40 && StructureWidth is >= 120 and <= 1200 && PropertiesWidth is >= 120 and <= 1200
        && Width is >= 600 and <= 10000 && Height is >= 400 and <= 10000;
}

/// <summary>Stores <see cref="PipelineEditorLayout"/> next to the main window's ui-layout.json; failures never block the editor.</summary>
public sealed class PipelineEditorLayoutStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;

    public PipelineEditorLayoutStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TestAgentSolution");
        _path = Path.Combine(directory, "pipeline-editor-layout.json");
    }

    public PipelineEditorLayout? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var layout = JsonSerializer.Deserialize<PipelineEditorLayout>(File.ReadAllText(_path));
            return layout?.IsUsable() == true ? layout : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(PipelineEditorLayout layout)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(layout, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a layout preference is not worth an error dialog.
        }
    }
}
