namespace VTD.Journal;

/// <summary>CRC-32 (IEEE 802.3, reflected polynomial 0xEDB88320), the checksum appended to every journal line.</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    /// <summary>Computes the CRC-32 of <paramref name="data"/>.</summary>
    /// <param name="data">Bytes.</param>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }
}
