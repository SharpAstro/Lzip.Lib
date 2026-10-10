using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace SharpAstro.Lzip;

/// <summary>
/// High-performance lzip (LZMA) decompressor for .NET.
/// Operates on in-memory buffers to avoid per-byte Stream.ReadByte() overhead.
/// Supports multi-member lzip files with parallel decompression.
/// </summary>
/// <remarks>
/// Each lzip member: 6-byte header + LZMA1 payload + 20-byte trailer.
/// Header: "LZIP" magic (4) + version (1) + dict size byte (1).
/// Trailer: CRC32 (4) + data size (8) + member size (8).
/// LZMA uses fixed properties for lzip: lc=3, lp=0, pb=2.
/// Multi-member files concatenate independent members that can be decompressed in parallel.
/// A member is believed only once its trailer agrees with what was decoded (the CRC32, the data size and the member
/// size, as the reference <c>lzip</c> checks them); anything else, a corrupt payload included, is an
/// <see cref="InvalidDataException"/>.
/// </remarks>
public static class LzipDecoder
{
    private const int HeaderSize = 6;
    private const int TrailerSize = 20;
    private const int MinMemberSize = HeaderSize + TrailerSize; // 26 bytes minimum

    public static byte[] Decompress(Stream input)
    {
        byte[] compressed;
        if (input.CanSeek)
        {
            var remaining = input.Length - input.Position;
            compressed = new byte[remaining];
            input.ReadExactly(compressed);
        }
        else
        {
            using var ms = new MemoryStream();
            input.CopyTo(ms);
            compressed = ms.ToArray();
        }

        return Decompress(compressed);
    }

    public static byte[] Decompress(byte[] data) => Decompress(data, 0, data.Length);

    public static byte[] Decompress(byte[] data, int offset, int length)
    {
        if (length < MinMemberSize)
        {
            throw new InvalidDataException("Data too short for lzip format.");
        }

        ValidateHeader(data, offset);

        // Find all members by reading trailers from the end
        var members = FindMembers(data, offset, length);

        if (members.Length == 1)
        {
            return DecompressSingleMember(data.AsSpan(offset, length));
        }

        // Multi-member: compute output offsets and decompress in parallel
        long totalSize = 0;
        foreach (var m in members)
        {
            totalSize += m.DataSize;
        }

        byte[] output = new byte[checked((int)totalSize)];
        var outputOffsets = new int[members.Length];
        int cumOffset = 0;
        for (int i = 0; i < members.Length; i++)
        {
            outputOffsets[i] = cumOffset;
            cumOffset += (int)members[i].DataSize;
        }

        try
        {
            Parallel.For(0, members.Length, i =>
            {
                var m = members[i];
                DecodeMember(data.AsSpan(m.Offset, m.Length), output, outputOffsets[i], (int)m.DataSize);
            });
        }
        catch (AggregateException ae) when (ae.InnerExceptions.Count > 0)
        {
            // Parallel.For wraps what a member threw: a corrupt member is the caller's InvalidDataException here
            // as on the single-member path, not a wrapper around it
            ExceptionDispatchInfo.Throw(ae.InnerExceptions[0]);
        }

        return output;
    }

    public static byte[] Decompress(ReadOnlySpan<byte> data)
    {
        if (data.Length < MinMemberSize)
        {
            throw new InvalidDataException("Data too short for lzip format.");
        }

        ValidateHeader(data);

        // Check if multi-member by reading first member's size from its trailer
        long firstMemberSize = BinaryPrimitives.ReadInt64LittleEndian(data[^8..]);

        if (firstMemberSize == data.Length)
        {
            return DecompressSingleMember(data);
        }

        // Multi-member: copy to array for Parallel.For support
        byte[] array = data.ToArray();
        return Decompress(array, 0, array.Length);
    }

    /// <summary>
    /// Decompresses all of <paramref name="input"/>, from its position to its end, into a read-only
    /// <see cref="MemoryStream"/>. The whole output is decoded before this returns (nothing is lazy), and
    /// <paramref name="input"/> is not disposed.
    /// </summary>
    public static MemoryStream DecompressToStream(Stream input)
    {
        return new MemoryStream(Decompress(input), writable: false);
    }

    private static byte[] DecompressSingleMember(ReadOnlySpan<byte> data)
    {
        long dataSize = ReadDataSize(data);

        byte[] output = new byte[checked((int)dataSize)];
        DecodeMember(data, output, 0, output.Length);

        return output;
    }

    // The trailer's data size, which sizes the output before anything is decoded; DecodeLzma then holds the stream
    // to it
    private static long ReadDataSize(ReadOnlySpan<byte> member)
    {
        long dataSize = BinaryPrimitives.ReadInt64LittleEndian(member[^16..^8]);
        if (dataSize < 0)
        {
            throw new InvalidDataException($"Invalid lzip data size: {dataSize}.");
        }
        return dataSize;
    }

    // One whole member (header, LZMA stream and trailer) into output[outputOffset..+outputLength], believed only once
    // the decoded bytes match the trailer's CRC32
    private static void DecodeMember(ReadOnlySpan<byte> member, byte[] output, int outputOffset, int outputLength)
    {
        uint dictSize = DecodeDictSize(member[5]);
        DecodeLzma(member[HeaderSize..^TrailerSize], dictSize, output, outputOffset, outputLength);

        uint expected = BinaryPrimitives.ReadUInt32LittleEndian(member[^TrailerSize..]);
        uint actual = LzipFraming.Crc32(output.AsSpan(outputOffset, outputLength));
        if (actual != expected)
        {
            throw new InvalidDataException($"lzip member CRC mismatch (trailer 0x{expected:X8}, data 0x{actual:X8}): the data is corrupt.");
        }
    }

    private readonly record struct MemberInfo(int Offset, int Length, long DataSize);

    private static MemberInfo[] FindMembers(byte[] data, int offset, int length)
    {
        // Walk backwards from end, reading member_size from each trailer
        var members = new System.Collections.Generic.List<MemberInfo>();
        int end = offset + length;

        while (end > offset)
        {
            if (end - offset < MinMemberSize)
            {
                throw new InvalidDataException("Truncated lzip member.");
            }

            long memberSize = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(end - 8, 8));

            // compared as a long BEFORE the cast: 0x1_FFFF_FFF0 truncates to -16 and put the start past the end
            if (memberSize < MinMemberSize || memberSize > end - offset)
            {
                throw new InvalidDataException($"Invalid lzip member size: {memberSize}.");
            }

            int memberStart = end - (int)memberSize;
            ValidateHeader(data, memberStart);

            long dataSize = ReadDataSize(data.AsSpan(memberStart, (int)memberSize));
            members.Add(new MemberInfo(memberStart, (int)memberSize, dataSize));

            end = memberStart;
        }

        members.Reverse();
        return [.. members];
    }

    private static void ValidateHeader(ReadOnlySpan<byte> data)
    {
        if (data[0] != 'L' || data[1] != 'Z' || data[2] != 'I' || data[3] != 'P')
        {
            throw new InvalidDataException("Invalid lzip magic bytes.");
        }

        if (data[4] != 1)
        {
            throw new InvalidDataException($"Unsupported lzip version {data[4]}.");
        }
    }

    private static void ValidateHeader(byte[] data, int offset)
    {
        ValidateHeader(data.AsSpan(offset, HeaderSize));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint DecodeDictSize(byte ds) => LzipFraming.DecodeDictSize(ds);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void DecodeLzma(ReadOnlySpan<byte> input, uint dictSize, byte[] output, int outputOffset, int outputLength)
    {
        // lzip fixed LZMA properties
        const int lc = 3, lp = 0, pb = 2;
        const int numPosStates = 1 << pb; // 4
        const int numLitStates = 1 << (lc + lp); // 8
        const int posMask = numPosStates - 1;

        const int kNumStates = 12;
        const int kNumPosSlotBits = 6;
        const int kNumLenToPosStates = 4;
        const int kEndPosModelIndex = 14;
        const int kNumAlignBits = 4;
        const int kNumFullDistances = 1 << (kEndPosModelIndex >> 1); // 128
        const int kMatchMinLen = 2;

        // Probability array layout (flat array with computed offsets)
        const int oIsMatch = 0;                                                     // 12 * 4 = 48
        const int oIsRep = oIsMatch + kNumStates * numPosStates;                    // 12
        const int oIsRepG0 = oIsRep + kNumStates;                                   // 12
        const int oIsRepG1 = oIsRepG0 + kNumStates;                                 // 12
        const int oIsRepG2 = oIsRepG1 + kNumStates;                                 // 12
        const int oIsRep0Long = oIsRepG2 + kNumStates;                              // 12 * 4 = 48
        const int oPosSlot = oIsRep0Long + kNumStates * numPosStates;               // 4 * 64 = 256
        const int oPosDecoders = oPosSlot + kNumLenToPosStates * (1 << kNumPosSlotBits); // 115
        const int oAlign = oPosDecoders + 1 + kNumFullDistances - kEndPosModelIndex; // 16
        const int oLenChoice = oAlign + (1 << kNumAlignBits);                       // 1
        const int oLenChoice2 = oLenChoice + 1;                                     // 1
        const int oLenLow = oLenChoice2 + 1;                                        // 4 * 8 = 32
        const int oLenMid = oLenLow + numPosStates * (1 << 3);                      // 4 * 8 = 32
        const int oLenHigh = oLenMid + numPosStates * (1 << 3);                     // 256
        const int oRepLenChoice = oLenHigh + (1 << 8);                              // 1
        const int oRepLenChoice2 = oRepLenChoice + 1;                               // 1
        const int oRepLenLow = oRepLenChoice2 + 1;                                  // 4 * 8 = 32
        const int oRepLenMid = oRepLenLow + numPosStates * (1 << 3);                // 4 * 8 = 32
        const int oRepLenHigh = oRepLenMid + numPosStates * (1 << 3);               // 256
        const int oLiteral = oRepLenHigh + (1 << 8);                                // 8 * 768 = 6144
        const int totalProbs = oLiteral + numLitStates * 0x300;

        // Initialize all probabilities to 1024 (50%)
        ushort[] probs = new ushort[totalProbs];
        probs.AsSpan().Fill(1024);

        // Initialize range decoder
        int inPos = 0;
        if (NextByte(input, ref inPos) != 0)
        {
            throw new InvalidDataException("Invalid LZMA stream (first byte must be 0).");
        }

        uint code = 0;
        for (int i = 0; i < 4; i++)
        {
            code = (code << 8) | NextByte(input, ref inPos);
        }

        uint range = 0xFFFFFFFF;

        // LZMA state
        int state = 0;
        uint rep0 = 0, rep1 = 0, rep2 = 0, rep3 = 0;
        int outPos = outputOffset;
        int outEnd = outputOffset + outputLength;

        // A cut or corrupt stream must fail as InvalidDataException, not as an index check, but a bounds check on
        // every byte DecodeBit reads cost a third of the decode (measured over TianWen's catalogues). So the loop reads the
        // member's own bytes while a whole symbol's worth is left, and the last few from a zero-padded copy, checking
        // once per symbol. A symbol reads at most a byte per bit: isMatch, isRep, 10 length, 6 slot, 26 direct and
        // 4 align bits.
        const int MaxBytesPerSymbol = 48;
        int inLength = input.Length;
        int inBase = 0; // where `input` starts in the member's stream: 0, then the tail copy's start
        int inLimit = inLength - MaxBytesPerSymbol; // a symbol starting at or before this reads no byte past the end
        bool inTail = false;

        // Decoded to the end-of-stream marker, which every lzip member carries, never to the trailer's data size:
        // a stream that would write past that size, or end short of it, is corrupt, and so is a distance reaching
        // back before the member's first byte
        while (true)
        {
            if (inPos > inLimit)
            {
                int rest = input.Length - inPos;
                if (inTail || rest < 0)
                {
                    throw new InvalidDataException("Truncated LZMA stream: it runs past its member's end.");
                }

                byte[] tail = new byte[rest + MaxBytesPerSymbol];
                input[inPos..].CopyTo(tail);
                input = tail;
                inBase += inPos;
                inPos = 0;
                inLimit = rest;
                inTail = true;
            }

            int relPos = outPos - outputOffset;
            int posState = relPos & posMask;

            if (DecodeBit(probs, oIsMatch + state * numPosStates + posState, ref range, ref code, input, ref inPos) == 0)
            {
                if (outPos == outEnd)
                {
                    throw LongerThanItsTrailer(outputLength);
                }

                // Literal
                int prevByte = outPos > outputOffset ? output[outPos - 1] : 0;
                int litState = ((relPos & ((1 << lp) - 1)) << lc) | (prevByte >> (8 - lc));
                int litBase = oLiteral + litState * 0x300;

                uint symbol = 1;

                if (state >= 7)
                {
                    // After match: use match byte for context
                    uint matchByte = (uint)(relPos > (int)rep0 ? output[outPos - (int)rep0 - 1] : 0);

                    do
                    {
                        uint matchBit = (matchByte >> 7) & 1;
                        matchByte <<= 1;
                        uint bit = (uint)DecodeBit(probs, litBase + (int)((1 + matchBit) << 8) + (int)symbol, ref range, ref code, input, ref inPos);
                        symbol = (symbol << 1) | bit;
                        if (matchBit != bit)
                        {
                            break;
                        }
                    } while (symbol < 0x100);

                    while (symbol < 0x100)
                    {
                        uint bit = (uint)DecodeBit(probs, litBase + (int)symbol, ref range, ref code, input, ref inPos);
                        symbol = (symbol << 1) | bit;
                    }
                }
                else
                {
                    do
                    {
                        uint bit = (uint)DecodeBit(probs, litBase + (int)symbol, ref range, ref code, input, ref inPos);
                        symbol = (symbol << 1) | bit;
                    } while (symbol < 0x100);
                }

                output[outPos++] = (byte)symbol;
                state = state < 4 ? 0 : state < 10 ? state - 3 : state - 6;
            }
            else
            {
                uint len;

                if (DecodeBit(probs, oIsRep + state, ref range, ref code, input, ref inPos) == 0)
                {
                    // Simple match
                    rep3 = rep2;
                    rep2 = rep1;
                    rep1 = rep0;

                    len = (uint)kMatchMinLen + DecodeLength(probs, oLenChoice, oLenLow, oLenMid, oLenHigh, posState, ref range, ref code, input, ref inPos);

                    // Decode distance
                    int lenState = (int)len - kMatchMinLen;
                    if (lenState >= kNumLenToPosStates)
                    {
                        lenState = kNumLenToPosStates - 1;
                    }

                    uint posSlot = DecodeBitTree(probs, oPosSlot + lenState * (1 << kNumPosSlotBits), kNumPosSlotBits, ref range, ref code, input, ref inPos);

                    if (posSlot < 4)
                    {
                        rep0 = posSlot;
                    }
                    else
                    {
                        int numDirectBits = (int)(posSlot >> 1) - 1;
                        rep0 = (2 | (posSlot & 1)) << numDirectBits;

                        if (posSlot < (uint)kEndPosModelIndex)
                        {
                            rep0 += DecodeBitTreeReverse(probs, oPosDecoders + (int)rep0 - (int)posSlot - 1, numDirectBits, ref range, ref code, input, ref inPos);
                        }
                        else
                        {
                            rep0 += DecodeDirectBits(numDirectBits - kNumAlignBits, ref range, ref code, input, ref inPos) << kNumAlignBits;
                            rep0 += DecodeBitTreeReverse(probs, oAlign, kNumAlignBits, ref range, ref code, input, ref inPos);
                        }
                    }

                    if (rep0 == 0xFFFFFFFF)
                    {
                        if (len != kMatchMinLen)
                        {
                            // len 3 is lzlib's sync flush marker, which lzip itself never writes
                            throw new InvalidDataException($"Unsupported LZMA marker (length {len}).");
                        }
                        break; // End of stream marker
                    }

                    if (rep0 >= dictSize)
                    {
                        throw new InvalidDataException($"LZMA distance {rep0 + 1} exceeds the member's dictionary of {dictSize} bytes.");
                    }

                    state = state < 7 ? 7 : 10;
                }
                else
                {
                    if (DecodeBit(probs, oIsRepG0 + state, ref range, ref code, input, ref inPos) == 0)
                    {
                        if (DecodeBit(probs, oIsRep0Long + state * numPosStates + posState, ref range, ref code, input, ref inPos) == 0)
                        {
                            // ShortRep: single byte at rep0 distance
                            if (outPos == outEnd)
                            {
                                throw LongerThanItsTrailer(outputLength);
                            }
                            if (rep0 >= (uint)relPos)
                            {
                                throw BeforeTheFirstByte(rep0, relPos);
                            }
                            state = state < 7 ? 9 : 11;
                            output[outPos] = output[outPos - (int)rep0 - 1];
                            outPos++;
                            continue;
                        }
                        // LongRep0: len bytes at rep0 distance
                    }
                    else
                    {
                        uint tmp;
                        if (DecodeBit(probs, oIsRepG1 + state, ref range, ref code, input, ref inPos) == 0)
                        {
                            tmp = rep1;
                        }
                        else
                        {
                            if (DecodeBit(probs, oIsRepG2 + state, ref range, ref code, input, ref inPos) == 0)
                            {
                                tmp = rep2;
                            }
                            else
                            {
                                tmp = rep3;
                                rep3 = rep2;
                            }
                            rep2 = rep1;
                        }
                        rep1 = rep0;
                        rep0 = tmp;
                    }

                    len = (uint)kMatchMinLen + DecodeLength(probs, oRepLenChoice, oRepLenLow, oRepLenMid, oRepLenHigh, posState, ref range, ref code, input, ref inPos);
                    state = state < 7 ? 8 : 11;
                }

                // Copy match bytes from dictionary
                if (rep0 >= (uint)relPos)
                {
                    throw BeforeTheFirstByte(rep0, relPos);
                }
                if (len > (uint)(outEnd - outPos))
                {
                    throw LongerThanItsTrailer(outputLength);
                }

                int dist = (int)rep0 + 1;
                int copyLen = (int)len;

                if (dist >= copyLen)
                {
                    // Non-overlapping: bulk copy
                    output.AsSpan(outPos - dist, copyLen).CopyTo(output.AsSpan(outPos));
                    outPos += copyLen;
                }
                else
                {
                    // Overlapping: byte-by-byte (handles RLE-like patterns)
                    for (int i = 0; i < copyLen; i++)
                    {
                        output[outPos] = output[outPos - dist];
                        outPos++;
                    }
                }
            }
        }

        if (outPos != outEnd)
        {
            throw new InvalidDataException($"lzip member ends after {outPos - outputOffset} bytes; its trailer says {outputLength}.");
        }

        // The range decoder reads exactly the bytes the encoder flushed, so a stream that ends before its member does
        // leaves bytes the member size claims and nothing decoded (and one that ran past the end read zeros)
        if (inBase + inPos != inLength)
        {
            throw new InvalidDataException($"lzip member size mismatch: the LZMA stream is {inBase + inPos} bytes, the member holds {inLength}.");
        }
    }

    private static InvalidDataException LongerThanItsTrailer(int outputLength)
        => new($"lzip member decodes to more than the {outputLength} bytes its trailer says.");

    private static InvalidDataException BeforeTheFirstByte(uint rep0, int relPos)
        => new($"LZMA distance {rep0 + 1} reaches back before the member's first byte (at byte {relPos}).");

    // The range decoder's first five bytes, read before the loop's check: past the end reads zeros, so a member too
    // short to hold them fails the size check as an InvalidDataException
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint NextByte(ReadOnlySpan<byte> input, ref int inPos)
    {
        int p = inPos++;
        return (uint)p < (uint)input.Length ? input[p] : 0u;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static int DecodeBit(ushort[] probs, int index, ref uint range, ref uint code, ReadOnlySpan<byte> input, ref int inPos)
    {
        uint prob = probs[index];
        uint bound = (range >> 11) * prob;

        if (code < bound)
        {
            range = bound;
            probs[index] = (ushort)(prob + ((2048 - prob) >> 5));
            if (range < 0x01000000)
            {
                range <<= 8;
                code = (code << 8) | input[inPos++];
            }
            return 0;
        }
        else
        {
            range -= bound;
            code -= bound;
            probs[index] = (ushort)(prob - (prob >> 5));
            if (range < 0x01000000)
            {
                range <<= 8;
                code = (code << 8) | input[inPos++];
            }
            return 1;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static uint DecodeLength(ushort[] probs, int choiceOffset, int lowOffset, int midOffset, int highOffset, int posState, ref uint range, ref uint code, ReadOnlySpan<byte> input, ref int inPos)
    {
        if (DecodeBit(probs, choiceOffset, ref range, ref code, input, ref inPos) == 0)
        {
            return DecodeBitTree(probs, lowOffset + posState * (1 << 3), 3, ref range, ref code, input, ref inPos);
        }

        if (DecodeBit(probs, choiceOffset + 1, ref range, ref code, input, ref inPos) == 0)
        {
            return 8 + DecodeBitTree(probs, midOffset + posState * (1 << 3), 3, ref range, ref code, input, ref inPos);
        }

        return 16 + DecodeBitTree(probs, highOffset, 8, ref range, ref code, input, ref inPos);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static uint DecodeBitTree(ushort[] probs, int baseOffset, int numBits, ref uint range, ref uint code, ReadOnlySpan<byte> input, ref int inPos)
    {
        uint m = 1;
        for (int i = 0; i < numBits; i++)
        {
            m = (m << 1) | (uint)DecodeBit(probs, baseOffset + (int)m, ref range, ref code, input, ref inPos);
        }
        return m - (1u << numBits);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static uint DecodeBitTreeReverse(ushort[] probs, int baseOffset, int numBits, ref uint range, ref uint code, ReadOnlySpan<byte> input, ref int inPos)
    {
        uint m = 1;
        uint symbol = 0;
        for (int i = 0; i < numBits; i++)
        {
            uint bit = (uint)DecodeBit(probs, baseOffset + (int)m, ref range, ref code, input, ref inPos);
            m = (m << 1) | bit;
            symbol |= bit << i;
        }
        return symbol;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static uint DecodeDirectBits(int numBits, ref uint range, ref uint code, ReadOnlySpan<byte> input, ref int inPos)
    {
        uint result = 0;
        for (int i = numBits - 1; i >= 0; i--)
        {
            range >>= 1;
            code -= range;
            uint t = 0u - (code >> 31);
            code += range & t;
            result = (result << 1) | (t + 1);

            if (range < 0x01000000)
            {
                range <<= 8;
                code = (code << 8) | input[inPos++];
            }
        }
        return result;
    }
}
