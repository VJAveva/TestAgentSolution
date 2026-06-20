using System.IO;
using TestAgent.Diagnostics.Models;

namespace TestAgent.Diagnostics.Services;

/// <summary>Loads and parses one or more log files into the shared record set (stream-parsed).</summary>
public sealed class LogLoader
{
    private readonly LogParser _parser = new();

    /// <summary>
    /// Parse all given files once into a single ordered list. Files are read line-by-line
    /// (File.ReadLines streams them) so the whole file is never held in memory at once.
    /// </summary>
    public IReadOnlyList<LogRecord> Load(IEnumerable<string> filePaths)
    {
        var records = new List<LogRecord>();
        foreach (var path in filePaths)
        {
            if (!File.Exists(path)) continue;
            var name = Path.GetFileName(path);
            try
            {
                foreach (var rec in _parser.Parse(File.ReadLines(path), name))
                    records.Add(rec);
            }
            catch (IOException)
            {
                // Skip a file that can't be read (locked / removed); other files still load.
            }
        }

        records.Sort(static (a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return records;
    }
}
