using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Instance adapter that delegates to the static <see cref="WatchListXmlParser"/> methods,
/// enabling DI registration and mocking via <see cref="IWatchListXmlParser"/>.
/// </summary>
public sealed class WatchListXmlParserService : IWatchListXmlParser
{
    public WatchListConfig Load(string filePath)
        => WatchListXmlParser.Load(filePath);

    public void Save(WatchListConfig config, string filePath)
        => WatchListXmlParser.Save(config, filePath);

    public string SerializeWatchItem(WatchItemConfig wi)
        => WatchListXmlParser.SerializeWatchItem(wi);

    public WatchItemConfig? DeserializeWatchItem(string xml)
        => WatchListXmlParser.DeserializeWatchItem(xml);

    public string SerializeTemplateList(List<TemplateConfig> templates)
        => WatchListXmlParser.SerializeTemplateList(templates);

    public List<TemplateConfig>? DeserializeTemplateList(string xml)
        => WatchListXmlParser.DeserializeTemplateList(xml);

    public string SerializeWatchList(WatchListConfig config)
        => WatchListXmlParser.SerializeWatchList(config);

    public WatchListConfig? DeserializeWatchList(string xml)
        => WatchListXmlParser.DeserializeWatchList(xml);

    public string SerializeWatchItemsToXml(List<WatchItemConfig> items, List<TemplateConfig>? referencedTemplates = null)
        => WatchListXmlParser.SerializeWatchItemsToXml(items, referencedTemplates);

    public string SerializeTemplatesToXml(List<TemplateConfig> templates)
        => WatchListXmlParser.SerializeTemplatesToXml(templates);

    public (List<WatchItemConfig> Items, List<TemplateConfig> Templates) ParseWatchItemsFromXml(string xml)
        => WatchListXmlParser.ParseWatchItemsFromXml(xml);

    public List<TemplateConfig> ParseTemplatesFromXml(string xml)
        => WatchListXmlParser.ParseTemplatesFromXml(xml);

    public HashSet<string> CollectRefTemplateIds(WatchItemConfig item)
        => WatchListXmlParser.CollectRefTemplateIds(item);
}
