using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Services;

/// <summary>
/// Thread-safe wrapper around WatchList XML file I/O.
/// Reads/writes via <see cref="IWatchListXmlParser"/> (from Core).
/// </summary>
public sealed class WatchListFileService
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private readonly IWatchListXmlParser _parser;
    private readonly IAppLogger _logger;

    public WatchListFileService(IConfiguration config, IWatchListXmlParser parser, IAppLogger logger)
    {
        _filePath = config["VocabularyFile"] ?? @"C:\TestControllerService\WatchList.xml";
        _parser = parser;
        _logger = logger;
    }

    public string FilePath => _filePath;

    public WatchListConfig Load()
    {
        lock (_lock)
        {
            try
            {
                var config = _parser.Load(_filePath);
                config.FilePath = _filePath;
                return config;
            }
            catch (Exception ex)
            {
                _logger.Error("WatchListFile", $"Failed to load WatchList from '{_filePath}'", ex);
                throw;
            }
        }
    }

    public void Save(WatchListConfig config)
    {
        lock (_lock)
        {
            try
            {
                _parser.Save(config, _filePath);
            }
            catch (Exception ex)
            {
                _logger.Error("WatchListFile", $"Failed to save WatchList to '{_filePath}'", ex);
                throw;
            }
        }
    }

    public string GetXml()
    {
        lock (_lock)
        {
            try
            {
                return File.ReadAllText(_filePath);
            }
            catch (Exception ex)
            {
                _logger.Error("WatchListFile", $"Failed to read XML from '{_filePath}'", ex);
                throw;
            }
        }
    }

    public bool Exists() => File.Exists(_filePath);
}
