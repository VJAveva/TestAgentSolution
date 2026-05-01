using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Parses and serializes the WatchList XML vocabulary file.
/// Provides instance methods that delegate to the static WatchListXmlParser
/// to enable dependency injection and mocking in tests.
/// </summary>
public interface IWatchListXmlParser
{
    WatchListConfig Load(string filePath);
    void Save(WatchListConfig config, string filePath);
    string SerializeWatchItem(WatchItemConfig wi);
    WatchItemConfig? DeserializeWatchItem(string xml);
    string SerializeTemplateList(List<TemplateConfig> templates);
    List<TemplateConfig>? DeserializeTemplateList(string xml);
    string SerializeWatchList(WatchListConfig config);
    WatchListConfig? DeserializeWatchList(string xml);
    string SerializeWatchItemsToXml(List<WatchItemConfig> items, List<TemplateConfig>? referencedTemplates = null);
    string SerializeTemplatesToXml(List<TemplateConfig> templates);
    (List<WatchItemConfig> Items, List<TemplateConfig> Templates) ParseWatchItemsFromXml(string xml);
    List<TemplateConfig> ParseTemplatesFromXml(string xml);
    HashSet<string> CollectRefTemplateIds(WatchItemConfig item);
}
