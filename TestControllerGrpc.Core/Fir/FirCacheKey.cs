using System.Security.Cryptography;
using System.Text;

namespace TestControllerGrpc.Core.Fir;

/// <summary>
/// Identifies the inputs a cached &lt;Test&gt;.fir.json was built from. A report must be recomputed when
/// the TRX, the log, the workbook OR a rule pack changes - a pack edit changes the verdict without
/// touching a single result file, so pack versions belong in the key.
/// </summary>
public static class FirCacheKey
{
    public static string For(
        string? trxPath, string? logPath, string? workbookPath, IEnumerable<string>? rulePacks)
    {
        var parts = string.Join('|',
            $"trx={Stamp(trxPath)}",
            $"log={Stamp(logPath)}",
            $"book={Stamp(workbookPath)}",
            $"packs={string.Join(',', rulePacks ?? [])}");

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(parts)))[..16];
    }

    /// <summary>Size as well as time: a restore can hand back an older file with a fresh timestamp.</summary>
    private static string Stamp(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "-";

        try
        {
            var file = new FileInfo(path);
            return file.Exists ? $"{file.LastWriteTimeUtc.Ticks}:{file.Length}" : "-";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "-";
        }
    }
}
