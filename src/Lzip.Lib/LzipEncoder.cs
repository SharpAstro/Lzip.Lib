using System;
using System.Buffers.Binary;
using System.IO;
using SevenZip;
using SevenZip.Compression.LZMA;

namespace SharpAstro.Lzip;

/// <summary>
/// lzip (LZMA1) compressor for .NET. Produces standard lzip files -- readable by
/// <see cref="LzipDecoder"/> and by the reference <c>lzip</c> binary -- with no external dependency.
/// </summary>
/// <remarks>
/// The LZMA1 compression core is the public-domain 7-Zip LZMA SDK (vendored under <c>Lzma/</c>);
/// this class adds the lzip container framing (header + trailer + CRC32) and multi-member
/// (<see cref="LzipOptions.MemberSize"/>) splitting. lzip's fixed LZMA properties (<c>lc=3, lp=0,
/// pb=2</c>) and the end-of-stream marker are always used, so the raw SDK stream is exactly the
/// lzip member payload (the SDK's separate 5-byte property / 8-byte size header is not written).
/// </remarks>
public static class LzipEncoder
{
    // lzip level table (main.cc option_mapping): { dictionary_size, match_len_limit } for -0..-9.
    private static readonly (int DictSize, int MatchLenLimit)[] _levels =
    [
        (65535,      16),  // -0
        (1 << 20,     5),  // -1
        (3 << 19,     6),  // -2
        (1 << 21,     8),  // -3
        (3 << 20,    12),  // -4
        (1 << 22,    20),  // -5
        (1 << 23,    36),  // -6 (lzip default)
        (1 << 24,    68),  // -7
        (3 << 23,   132),  // -8
        (1 << 25,   273),  // -9
    ];

    /// <summary>Compresses <paramref name="data"/> to a byte array holding a complete lzip file.</summary>
    public static byte[] Compress(byte[] data, LzipOptions? options = null)
    {
        using var output = new MemoryStream();
        CompressCore(data, 0, data.Length, output, options ?? LzipOptions.Default);
        return output.ToArray();
    }

    /// <summary>Compresses <paramref name="data"/> to a byte array holding a complete lzip file.</summary>
    public static byte[] Compress(ReadOnlySpan<byte> data, LzipOptions? options = null)
        => Compress(data.ToArray(), options);

    /// <summary>Reads all of <paramref name="input"/> and writes a complete lzip file to <paramref name="output"/>.</summary>
    public static void Compress(Stream input, Stream output, LzipOptions? options = null)
    {
        byte[] data = ReadAllBytes(input);
        CompressCore(data, 0, data.Length, output, options ?? LzipOptions.Default);
    }

    private static void CompressCore(byte[] data, int start, int length, Stream output, LzipOptions options)
    {
        long memberSize = options.MemberSize;

        // Single member: no split (also the empty-input case -- one valid empty member).
        if (memberSize <= 0 || length <= memberSize)
        {
            WriteMember(data, start, length, output, options);
            return;
        }

        // Multi-member: independent members of ~memberSize uncompressed bytes each.
        int offset = start;
        int end = start + length;
        while (offset < end)
        {
            int chunk = (int)Math.Min(memberSize, end - offset);
            WriteMember(data, offset, chunk, output, options);
            offset += chunk;
        }
    }

    private static void WriteMember(byte[] data, int start, int length, Stream output, LzipOptions options)
    {
        ResolveParameters(options, length, out uint requestedDict, out int matchLenLimit);
        byte dictSizeByte = LzipFraming.EncodeDictSize(requestedDict, out uint actualDict);

        // Compress the member payload with the SDK encoder into a temp buffer so we can measure the
        // stream length (needed for the trailer's member_size before writing the trailer).
        using var lzmaStream = new MemoryStream();
        var encoder = new Encoder();
        encoder.SetCoderProperties(_propIds, BuildProperties(actualDict, matchLenLimit));
        using (var memberInput = new MemoryStream(data, start, length, writable: false))
        {
            encoder.Code(memberInput, lzmaStream, length, -1, null);
        }

        int lzmaLength = (int)lzmaStream.Length;
        long totalMemberSize = LzipFraming.HeaderSize + (long)lzmaLength + LzipFraming.TrailerSize;

        // Header: "LZIP" + version 1 + dict-size byte.
        Span<byte> header = stackalloc byte[LzipFraming.HeaderSize];
        header[0] = (byte)'L';
        header[1] = (byte)'Z';
        header[2] = (byte)'I';
        header[3] = (byte)'P';
        header[4] = 1;
        header[5] = dictSizeByte;
        output.Write(header);

        // LZMA1 payload.
        output.Write(lzmaStream.GetBuffer(), 0, lzmaLength);

        // Trailer: CRC32(uncompressed) + data_size (u64 LE) + member_size (u64 LE).
        Span<byte> trailer = stackalloc byte[LzipFraming.TrailerSize];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[0..4], LzipFraming.Crc32(data.AsSpan(start, length)));
        BinaryPrimitives.WriteInt64LittleEndian(trailer[4..12], length);
        BinaryPrimitives.WriteInt64LittleEndian(trailer[12..20], totalMemberSize);
        output.Write(trailer);
    }

    private static void ResolveParameters(LzipOptions options, int dataLength, out uint dictSize, out int matchLenLimit)
    {
        int level = Math.Clamp(options.Level, 0, _levels.Length - 1);
        (int levelDict, int levelMll) = _levels[level];

        long chosen;
        if (options.DictionarySize is int explicitDict)
        {
            chosen = explicitDict;
        }
        else
        {
            // Cap the dictionary to the input size (as lzip does) so small members don't declare a
            // needlessly large dictionary; never below the minimum.
            long cap = Math.Max(dataLength, (long)LzipFraming.MinDictionarySize);
            chosen = Math.Min(levelDict, cap);
        }

        dictSize = (uint)Math.Clamp(chosen, LzipFraming.MinDictionarySize, LzipFraming.MaxDictionarySize);
        matchLenLimit = Math.Clamp(options.MatchLenLimit ?? levelMll, LzipFraming.MinMatchLenLimit, LzipFraming.MaxMatchLen);
    }

    // Property IDs passed to every encoder. lzip fixes pb=2, lc=3, lp=0 and always writes the
    // end-of-stream marker; BT4 is the SDK's best match finder.
    private static readonly CoderPropID[] _propIds =
    [
        CoderPropID.DictionarySize,
        CoderPropID.PosStateBits,
        CoderPropID.LitContextBits,
        CoderPropID.LitPosBits,
        CoderPropID.NumFastBytes,
        CoderPropID.MatchFinder,
        CoderPropID.EndMarker,
    ];

    private static object[] BuildProperties(uint dictSize, int matchLenLimit) =>
    [
        (int)dictSize,     // DictionarySize
        2,                 // PosStateBits (pb)
        3,                 // LitContextBits (lc)
        0,                 // LitPosBits (lp)
        matchLenLimit,     // NumFastBytes
        "BT4",             // MatchFinder
        true,              // EndMarker
    ];

    private static byte[] ReadAllBytes(Stream input)
    {
        if (input is MemoryStream ms)
        {
            return ms.ToArray();
        }
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        return buffer.ToArray();
    }
}
