using System;

namespace SharpAstro.Lzip;

/// <summary>
/// Shared lzip container framing: format constants, the dictionary-size byte codec, and the
/// CRC32 used by member trailers. Used by both <see cref="LzipDecoder"/> and <see cref="LzipEncoder"/>
/// so the read and write sides can never drift.
/// </summary>
/// <remarks>
/// Member layout: 6-byte header (<c>"LZIP"</c> + version <c>1</c> + coded dict-size byte) + LZMA1
/// stream (fixed <c>lc=3, lp=0, pb=2</c>) + 20-byte trailer (CRC32 of the uncompressed data +
/// uncompressed size <c>u64</c> + member size <c>u64</c>, all little-endian).
/// </remarks>
internal static class LzipFraming
{
    public const int HeaderSize = 6;
    public const int TrailerSize = 20;
    public const int MinMemberSize = HeaderSize + TrailerSize; // 26 bytes minimum

    // lzip dictionary-size bounds (lzip.h: min_dictionary_bits=12, max_dictionary_bits=29).
    public const uint MinDictionarySize = 1u << 12;  // 4 KiB
    public const uint MaxDictionarySize = 1u << 29;  // 512 MiB

    // lzip match-length-limit ("fast bytes") bounds.
    public const int MinMatchLenLimit = 5;
    public const int MaxMatchLen = 273;

    /// <summary>Decodes the lzip dictionary-size byte to a size in bytes.</summary>
    public static uint DecodeDictSize(byte ds)
    {
        uint d = 1u << (ds & 0x1F);
        return d - (d >> 4) * ((uint)(ds >> 5) & 7);
    }

    /// <summary>
    /// Encodes a dictionary size to the lzip dictionary-size byte, returning the actual
    /// (representable) size the encoder must use via <paramref name="actual"/>.
    /// </summary>
    /// <remarks>
    /// Mirrors lzip's <c>Lzip_header::dictionary_size(sz)</c>: take base bits = number of bits in
    /// <c>sz-1</c>, then subtract the largest wedge fraction <c>i/16</c> (i in 1..7) that stays
    /// &gt;= the requested size. The result is the smallest lzip-representable size &gt;= <paramref name="size"/>,
    /// so the declared dictionary matches what the decoder will allocate (verified against
    /// <see cref="DecodeDictSize"/> and the real lzip binary).
    /// </remarks>
    public static byte EncodeDictSize(uint size, out uint actual)
    {
        if (size < MinDictionarySize)
        {
            size = MinDictionarySize;
        }
        else if (size > MaxDictionarySize)
        {
            size = MaxDictionarySize;
        }

        int bits = RealBits(size - 1);
        byte ds = (byte)bits;
        uint baseSize = 1u << bits;

        if (size > MinDictionarySize)
        {
            uint fraction = baseSize >> 4; // baseSize / 16
            for (uint i = 7; i >= 1; i--)
            {
                if (baseSize - i * fraction >= size)
                {
                    ds |= (byte)(i << 5);
                    break;
                }
            }
        }

        actual = DecodeDictSize(ds);
        return ds;
    }

    /// <summary>Number of bits required to represent <paramref name="n"/> (0 for n == 0).</summary>
    private static int RealBits(uint n)
    {
        int bits = 0;
        while (n > 0)
        {
            n >>= 1;
            bits++;
        }
        return bits;
    }

    // Standard CRC32 (reflected, polynomial 0xEDB88320) -- identical to zlib/gzip and to lzip's
    // member trailer CRC over the uncompressed data.
    private static readonly uint[] _crcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }

    /// <summary>CRC32 (reflected, poly 0xEDB88320) of <paramref name="data"/>.</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            c = _crcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }
        return c ^ 0xFFFFFFFFu;
    }
}
