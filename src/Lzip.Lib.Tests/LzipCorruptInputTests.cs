using System;
using System.Buffers.Binary;
using System.IO;
using Shouldly;
using SharpAstro.Lzip;
using Xunit;

namespace Lzip.Lib.Tests;

/// <summary>
/// A member is believed only once its trailer agrees with what was decoded: the CRC32, the data size and the
/// member size, which the reference <c>lzip</c> checks too. Every corruption is an
/// <see cref="InvalidDataException"/>, never an index or range exception, an <see cref="AggregateException"/> from
/// the parallel path, or output that is silently wrong.
/// </summary>
public class LzipCorruptInputTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    // (offset, length) of each member, walked back from the end through each trailer's member_size
    private static (int Offset, int Length)[] Members(byte[] lz)
    {
        var members = new System.Collections.Generic.List<(int, int)>();
        for (int end = lz.Length; end > 0;)
        {
            int size = (int)BinaryPrimitives.ReadInt64LittleEndian(lz.AsSpan(end - 8));
            members.Insert(0, (end - size, size));
            end -= size;
        }
        return [.. members];
    }

    [Fact]
    public void AWrongTrailerCrcIsRefused()
    {
        byte[] lz = Fixture("single.lz");
        lz[^20] ^= 0xFF;

        Should.Throw<InvalidDataException>(() => LzipDecoder.Decompress(lz));
    }

    [Fact]
    public void AWrongCrcInOneMemberOfManyIsRefused()
    {
        byte[] lz = Fixture("multi.lz");
        var (offset, length) = Members(lz)[2];
        lz[offset + length - 20] ^= 0xFF;

        Should.Throw<InvalidDataException>(() => LzipDecoder.Decompress(lz));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(-100)]
    public void ADataSizeTheStreamDisagreesWithIsRefused(int delta)
    {
        byte[] lz = Fixture("single.lz");
        var dataSize = lz.AsSpan(lz.Length - 16, 8);
        BinaryPrimitives.WriteInt64LittleEndian(dataSize, BinaryPrimitives.ReadInt64LittleEndian(dataSize) + delta);

        Should.Throw<InvalidDataException>(() => LzipDecoder.Decompress(lz));
    }

    [Fact]
    public void ANegativeDataSizeIsRefused()
    {
        byte[] lz = Fixture("single.lz");
        BinaryPrimitives.WriteInt64LittleEndian(lz.AsSpan(lz.Length - 16, 8), -1);

        Should.Throw<InvalidDataException>(() => LzipDecoder.Decompress(lz));
    }

    [Fact]
    public void AMemberSizeLargerThanTheFileIsRefused()
    {
        // 0x1_FFFF_FFF0 truncates to -16 as an int, which once put the member's start past its end
        byte[] lz = Fixture("multi.lz");
        BinaryPrimitives.WriteInt64LittleEndian(lz.AsSpan(lz.Length - 8, 8), 0x1_FFFF_FFF0);

        Should.Throw<InvalidDataException>(() => LzipDecoder.Decompress(lz));
    }

    [Fact]
    public void ACorruptMemberOnTheParallelPathIsRefusedUnwrapped()
    {
        byte[] lz = Fixture("multi.lz");
        lz[Members(lz)[0].Offset + 6] = 1; // the LZMA stream's first byte must be 0

        Should.Throw<InvalidDataException>(() => LzipDecoder.Decompress(lz));
    }

    [Fact]
    public void ABytePastTheStreamsEndIsRefused()
    {
        // one byte more before the trailer, with the member size saying so: the stream ends before its member does
        byte[] lz = Fixture("single.lz");
        byte[] longer = [.. lz[..^20], 0, .. lz[^20..]];
        BinaryPrimitives.WriteInt64LittleEndian(longer.AsSpan(longer.Length - 8, 8), longer.Length);

        Should.Throw<InvalidDataException>(() => LzipDecoder.Decompress(longer));
    }

    [Fact]
    public void AStreamCutShortIsRefused()
    {
        byte[] lz = Fixture("single.lz");
        int payload = lz.Length - 26;
        byte[] cut = [.. lz[..(6 + payload / 2)], .. lz[^20..]];
        BinaryPrimitives.WriteInt64LittleEndian(cut.AsSpan(cut.Length - 8, 8), cut.Length);

        Should.Throw<InvalidDataException>(() => LzipDecoder.Decompress(cut));
    }

    [Theory]
    [InlineData("single.lz", 1)]
    [InlineData("multi.lz", 37)]
    public void EveryFlippedPayloadBitIsRefusedOrChangesNothing(string fixture, int stride)
    {
        // a flip in the range coder's last flushed bits can leave every decoded symbol as it was, so the only
        // other acceptable answer is the original data, exactly
        byte[] lz = Fixture(fixture);
        byte[] expected = LzipDecoder.Decompress(lz);
        int refused = 0, unchanged = 0;

        foreach (var (offset, length) in Members(lz))
        {
            for (int bit = 0; bit < (length - 26) * 8; bit += stride)
            {
                byte[] flipped = [.. lz];
                flipped[offset + 6 + bit / 8] ^= (byte)(1 << (bit % 8));
                try
                {
                    LzipDecoder.Decompress(flipped).ShouldBe(expected, $"bit {bit} of the member at {offset} decoded to other data");
                    unchanged++;
                }
                catch (InvalidDataException)
                {
                    refused++;
                }
            }
        }

        refused.ShouldBeGreaterThan(unchanged * 10);
    }
}
