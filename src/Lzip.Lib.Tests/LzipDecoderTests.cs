using System;
using System.IO;
using System.Linq;
using System.Text;
using Shouldly;
using SharpAstro.Lzip;
using Xunit;

namespace Lzip.Lib.Tests;

/// <summary>
/// Decodes golden <c>.lz</c> fixtures produced by the real <c>lzip</c> binary (see <c>Fixtures/</c>),
/// exercising the single-member fast path and the multi-member parallel path against the exact
/// deterministic payloads the fixtures were built from.
/// </summary>
public class LzipDecoderTests
{
    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    // Payload that single.lz was built from (39 chars x 200 = 7800 bytes).
    private static byte[] SingleExpected()
        => Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("Lzip.Lib decodes lzip (LZMA1) members.\n", 200)));

    // Payload that multi.lz was built from: 500000 bytes of (i*131+7) mod 256, split into five
    // ~100 kB lzip members (concatenated).
    private static byte[] MultiExpected()
    {
        var expected = new byte[500000];
        for (var i = 0; i < expected.Length; i++)
        {
            expected[i] = (byte)((i * 131 + 7) & 0xFF);
        }
        return expected;
    }

    [Fact]
    public void Decompress_SingleMember_FromByteArray()
        => LzipDecoder.Decompress(File.ReadAllBytes(FixturePath("single.lz"))).ShouldBe(SingleExpected());

    [Fact]
    public void Decompress_SingleMember_FromStream()
    {
        using var stream = File.OpenRead(FixturePath("single.lz"));
        LzipDecoder.Decompress(stream).ShouldBe(SingleExpected());
    }

    [Fact]
    public void Decompress_SingleMember_FromSpan()
    {
        ReadOnlySpan<byte> span = File.ReadAllBytes(FixturePath("single.lz"));
        LzipDecoder.Decompress(span).ShouldBe(SingleExpected());
    }

    [Fact]
    public void Decompress_MultiMember_ParallelPath()
        => LzipDecoder.Decompress(File.ReadAllBytes(FixturePath("multi.lz"))).ShouldBe(MultiExpected());

    [Fact]
    public void DecompressToStream_YieldsSameBytes()
    {
        using var output = LzipDecoder.DecompressToStream(File.OpenRead(FixturePath("single.lz")));
        output.ToArray().ShouldBe(SingleExpected());
    }
}
