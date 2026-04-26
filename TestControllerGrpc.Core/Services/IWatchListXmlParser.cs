using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Parses and serializes the WatchList XML vocabulary file.
/// Provides instance methods that delegate to the static WatchListXmlParser
/// to enable dependency injection and mocking in tests.
/// </summary>
public interface IWatchListXmlParser
{
    /// <summary>Loads and deserializes a complete WatchList configuration from an XML file on disk.</summary>
    /// <param name="filePath">Absolute or relative path to the WatchList XML file.</param>
    /// <returns>The deserialized <see cref="WatchListConfig"/>.</returns>
    /// <exception cref="System.IO.FileNotFoundException">Thrown when <paramref name="filePath"/> does not exist.</exception>
    /// <exception cref="System.Xml.XmlException">Thrown when the file contains malformed XML.</exception>
    WatchListConfig Load(string filePath);

    /// <summary>Serializes a WatchList configuration and writes it to the specified file path.</summary>
    /// <param name="config">The configuration to serialize.</param>
    /// <param name="filePath">The destination XML file path.</param>
    void Save(WatchListConfig config, string filePath);

    /// <summary>Serializes a single <see cref="WatchItemConfig"/> to its XML representation.</summary>
    string SerializeWatchItem(WatchItemConfig wi);

    /// <summary>Parses an XML fragment into a single <see cref="WatchItemConfig"/>.</summary>
    /// <returns>The deserialized item, or <c>null</c> if the XML is empty or invalid.</returns>
    WatchItemConfig? DeserializeWatchItem(string xml);

    /// <summary>Serializes a list of templates to XML.</summary>
    string SerializeTemplateList(List<TemplateConfig> templates);

    /// <summary>Parses an XML fragment into a list of templates.</summary>
    /// <returns>The deserialized templates, or <c>null</c> if the XML is empty or invalid.</returns>
    List<TemplateConfig>? DeserializeTemplateList(string xml);

    /// <summary>Serializes the entire <see cref="WatchListConfig"/> document to XML.</summary>
    string SerializeWatchList(WatchListConfig config);

    /// <summary>Parses a complete WatchList XML document.</summary>
    /// <returns>The deserialized configuration, or <c>null</c> on failure.</returns>
    WatchListConfig? DeserializeWatchList(string xml);

    /// <summary>
    /// Serializes the supplied watch items (and optionally only the referenced templates)
    /// into a self-contained XML document suitable for export.
    /// </summary>
    string SerializeWatchItemsToXml(List<WatchItemConfig> items, List<TemplateConfig>? referencedTemplates = null);

    /// <summary>Serializes a list of templates into a self-contained XML document for export.</summary>
    string SerializeTemplatesToXml(List<TemplateConfig> templates);

    /// <summary>
    /// Parses an exported WatchList XML document into its constituent items and templates.
    /// </summary>
    /// <returns>A tuple containing the parsed watch items and any embedded templates.</returns>
    (List<WatchItemConfig> Items, List<TemplateConfig> Templates) ParseWatchItemsFromXml(string xml);

    /// <summary>Parses an XML document containing only template definitions.</summary>
    List<TemplateConfig> ParseTemplatesFromXml(string xml);

    /// <summary>
    /// Recursively collects the identifiers of every template referenced (directly or
    /// transitively via <c>&lt;Ref&gt;</c> nodes) by the specified watch item.
    /// </summary>
    /// <param name="item">The watch item to inspect.</param>
    /// <returns>A set of template identifiers.</returns>
    HashSet<string> CollectRefTemplateIds(WatchItemConfig item);
}
