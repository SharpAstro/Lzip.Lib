using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using SharpAstro.Lzip;
using Xunit;

namespace Lzip.Lib.Tests;

/// <summary>
/// Exercises <see cref="LzipEncoder"/> via the verification triangle: (1) our-encode -> our-decode
/// round-trips for a range of payloads/levels and multi-member splits; (2) the emitted container has
/// valid lzip framing; (3) the real <c>lzip</c> binary accepts and decompresses our output (the
/// "real lzip reads us" proof, skipped when <c>lzip</c> is not installed).
/// </summary>
public class LzipEncoderTests
{
    // ---- payloads ----

    private static byte[] Payload(string kind) => kind switch
    {
        "empty" => [],
        "single" => [0x42],
        "text" => System.Text.Encoding.ASCII.GetBytes(
            string.Concat(System.Linq.Enumerable.Repeat("Lzip.Lib round-trips lzip (LZMA1) members.\n", 200))),
        "zeros" => new byte[300_000],
        "pseudorandom" => Pseudorandom(200_000),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    // Deterministic, poorly-compressible bytes (LCG) -- no Random (keeps the test reproducible).
    private static byte[] Pseudorandom(int length)
    {
        var data = new byte[length];
        uint state = 0x1234_5678;
        for (int i = 0; i < length; i++)
        {
            state = state * 1664525 + 1013904223;
            data[i] = (byte)(state >> 24);
        }
        return data;
    }

    // ---- round-trip (our encode -> our decode) ----

    [Theory]
    [InlineData("empty", 9)]
    [InlineData("single", 9)]
    [InlineData("text", 0)]
    [InlineData("text", 6)]
    [InlineData("text", 9)]
    [InlineData("zeros", 9)]
    [InlineData("pseudorandom", 1)]
    [InlineData("pseudorandom", 9)]
    public void RoundTrip_OurEncode_OurDecode(string kind, int level)
    {
        byte[] data = Payload(kind);
        byte[] compressed = LzipEncoder.Compress(data, new LzipOptions { Level = level });

        LzipDecoder.Decompress(compressed).ShouldBe(data);
    }

    [Fact]
    public void Compress_Span_And_ByteArray_Agree()
    {
        byte[] data = Payload("text");
        LzipEncoder.Compress(data.AsSpan()).ShouldBe(LzipEncoder.Compress(data));
    }

    [Fact]
    public void Compress_StreamOverload_RoundTrips()
    {
        byte[] data = Payload("pseudorandom");
        using var input = new MemoryStream(data);
        using var output = new MemoryStream();
        LzipEncoder.Compress(input, output);

        LzipDecoder.Decompress(output.ToArray()).ShouldBe(data);
    }

    // ---- framing sanity ----

    [Fact]
    public void Compress_EmitsValidLzipHeader()
    {
        byte[] compressed = LzipEncoder.Compress(Payload("text"));

        compressed.Length.ShouldBeGreaterThanOrEqualTo(26); // min member size
        compressed[0].ShouldBe((byte)'L');
        compressed[1].ShouldBe((byte)'Z');
        compressed[2].ShouldBe((byte)'I');
        compressed[3].ShouldBe((byte)'P');
        compressed[4].ShouldBe((byte)1); // version
    }

    [Fact]
    public void Compress_CompressibleInput_ShrinksSignificantly()
    {
        byte[] data = Payload("zeros");
        byte[] compressed = LzipEncoder.Compress(data);

        compressed.Length.ShouldBeLessThan(data.Length / 10);
    }

    // ---- multi-member (-b style) ----

    [Fact]
    public void Compress_MemberSize_ProducesMultipleMembers_ThatRoundTrip()
    {
        byte[] data = Pseudorandom(500_000);
        byte[] compressed = LzipEncoder.Compress(data, new LzipOptions { MemberSize = 100_000 });

        CountMembers(compressed).ShouldBe(5);
        LzipDecoder.Decompress(compressed).ShouldBe(data); // exercises the decoder's parallel path
    }

    [Fact]
    public void Compress_MemberSizeLargerThanInput_ProducesSingleMember()
    {
        byte[] data = Payload("text");
        byte[] compressed = LzipEncoder.Compress(data, new LzipOptions { MemberSize = 10_000_000 });

        CountMembers(compressed).ShouldBe(1);
        LzipDecoder.Decompress(compressed).ShouldBe(data);
    }

    // Walks the concatenated members backwards via each trailer's member_size (last 8 bytes).
    private static int CountMembers(byte[] lzip)
    {
        int end = lzip.Length;
        int count = 0;
        while (end > 0)
        {
            long memberSize = BitConverter.ToInt64(lzip, end - 8);
            end -= (int)memberSize;
            count++;
            (end >= 0).ShouldBeTrue("member walk underflowed -- corrupt framing");
        }
        return count;
    }

    // ---- oracle: real lzip must accept and decompress our output ----

    [Theory]
    [InlineData("text", 9, 0)]
    [InlineData("pseudorandom", 9, 0)]
    [InlineData("zeros", 6, 0)]
    [InlineData("pseudorandom", 9, 100_000)] // multi-member
    public async Task RealLzip_Accepts_And_Decompresses_OurOutput(string kind, int level, long memberSize)
    {
        byte[] data = Payload(kind);
        byte[] compressed = LzipEncoder.Compress(data, new LzipOptions { Level = level, MemberSize = memberSize });

        // Integrity check: `lzip -t` must return 0.
        var (integrityOk, exit, _) = await TryRunLzipAsync("-t", compressed);
        if (!integrityOk)
        {
            Assert.Skip("lzip binary not found on PATH; skipping oracle cross-decode.");
        }
        exit.ShouldBe(0, "real lzip -t rejected our output");

        // Decompress with real lzip and compare byte-for-byte.
        var (ok, decExit, decoded) = await TryRunLzipAsync("-dc", compressed);
        ok.ShouldBeTrue();
        decExit.ShouldBe(0, "real lzip -dc failed on our output");
        decoded.ShouldBe(data);
    }

    /// <summary>
    /// Runs <c>lzip &lt;args&gt;</c> with <paramref name="stdin"/> piped in, returning
    /// (found, exitCode, stdoutBytes). <c>found == false</c> when the binary is absent.
    /// </summary>
    private static async Task<(bool Found, int Exit, byte[] Stdout)> TryRunLzipAsync(string args, byte[] stdin)
    {
        var psi = new ProcessStartInfo("lzip", args)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception)
        {
            return (false, -1, []); // lzip not installed
        }

        if (process is null)
        {
            return (false, -1, []);
        }

        using (process)
        {
            // Read stdout on a background task while feeding stdin to avoid a full-pipe deadlock.
            var stdoutTask = Task.Run(async () =>
            {
                using var buffer = new MemoryStream();
                await process.StandardOutput.BaseStream.CopyToAsync(buffer);
                return buffer.ToArray();
            });

            await process.StandardInput.BaseStream.WriteAsync(stdin);
            process.StandardInput.Close();

            byte[] stdout = await stdoutTask;
            await process.WaitForExitAsync();
            return (true, process.ExitCode, stdout);
        }
    }
}
