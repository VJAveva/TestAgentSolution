using System.Runtime.InteropServices;

namespace TestControllerGrpc.Core.Impact.Index;

/// <summary>
/// Converts embedding vectors to and from the compact byte layout stored in the
/// <see cref="DocumentVector.Vector"/> BLOB column (4 bytes per element). The index is a host-local,
/// rebuildable artifact and is always written and read on the same machine, so the machine-order
/// reinterpretation used here is safe (x64 and ARM64 are both little-endian).
/// </summary>
public static class VectorBlob
{
    /// <summary>Packs a float vector into a BLOB. Returns an empty array for an empty input.</summary>
    public static byte[] FromFloats(ReadOnlySpan<float> values)
        => values.IsEmpty ? Array.Empty<byte>() : MemoryMarshal.AsBytes(values).ToArray();

    /// <summary>Unpacks a BLOB back into a float vector.</summary>
    /// <exception cref="ArgumentException">The blob length is not a whole number of 4-byte floats.</exception>
    public static float[] ToFloats(ReadOnlySpan<byte> blob)
    {
        if (blob.IsEmpty)
        {
            return Array.Empty<float>();
        }

        if (blob.Length % sizeof(float) != 0)
        {
            throw new ArgumentException(
                $"Vector blob length {blob.Length} is not a multiple of {sizeof(float)}.", nameof(blob));
        }

        return MemoryMarshal.Cast<byte, float>(blob).ToArray();
    }
}
