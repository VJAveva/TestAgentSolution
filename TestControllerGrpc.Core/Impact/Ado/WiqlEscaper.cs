namespace TestControllerGrpc.Core.Impact.Ado;

/// <summary>WIQL safety helper: escapes literals and rejects control characters so a value can never break
/// out of a quoted WIQL string.</summary>
public static class WiqlEscaper
{
    /// <summary>Doubles single quotes for safe inclusion in a WIQL string literal.</summary>
    /// <exception cref="ArgumentException">Thrown when the value contains a control character or newline.</exception>
    public static string EscapeLiteral(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        foreach (var c in value)
            if (char.IsControl(c))
                throw new ArgumentException("WIQL literal contains a control character or newline.", nameof(value));
        return value.Replace("'", "''");
    }
}
