using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Services;

/// <summary>
/// Thread-safe wrapper around WatchList XML file I/O.
/// Reads/writes via <see cref="WatchListXmlParser"/> (from Core).
/// </summary>
public sealed class WatchListFileService
{
    private readonly string _filePath;
    private readonly object _lock = new();

    public WatchListFileService(IConfiguration config)
    {
        _filePath = config["VocabularyFile"] ?? @"C:\TestControllerService\WatchList.xml";
    }

    public string FilePath => _filePath;

    public WatchListConfig Load()
    {
        lock (_lock)
        {
            var config = WatchListXmlParser.Load(_filePath);
            config.FilePath = _filePath;
            return config;
        }
    }

    public void Save(WatchListConfig config)
    {
        lock (_lock)
            WatchListXmlParser.Save(config, _filePath);
    }

    public string GetXml()
    {
        lock (_lock)
            return File.ReadAllText(_filePath);
    }

    public bool Exists() => File.Exists(_filePath);
}
