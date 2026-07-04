# Lzip.Lib

Pure-managed **lzip** (LZMA1) codec for .NET -- a fast, AOT/trim-friendly compressor and
decompressor for the [lzip](https://www.nongnu.org/lzip/) container format, including multi-member
files that are decoded in parallel. Output is read by the reference `lzip` binary. No external
`lzip` binary required.

`1.0` shipped the **decoder**; `1.1` adds the **encoder** (LZMA1 + lzip member framing +
`-b`-style member splitting).

## Install

```
dotnet add package Lzip.Lib
```

## Usage

```csharp
using SharpAstro.Lzip;

// From a byte array:
byte[] data = LzipDecoder.Decompress(File.ReadAllBytes("catalog.lz"));

// From a stream:
using var stream = File.OpenRead("catalog.lz");
byte[] fromStream = LzipDecoder.Decompress(stream);

// Lazily as a stream:
using var decompressed = LzipDecoder.DecompressToStream(File.OpenRead("catalog.lz"));
```

Multi-member lzip files (e.g. produced by `lzip -b`) are detected and decoded across cores
automatically.

### Compressing

```csharp
using SharpAstro.Lzip;

// Best ratio (approximates `lzip -9`), single member:
byte[] lz = LzipEncoder.Compress(File.ReadAllBytes("catalog.json"));

// Choose a level (0..9) and split into independent members for parallel decode (like `lzip -b`):
byte[] chunked = LzipEncoder.Compress(data, new LzipOptions { Level = 9, MemberSize = 2 << 20 });

// Stream in / stream out:
using var input = File.OpenRead("catalog.json");
using var output = File.Create("catalog.json.lz");
LzipEncoder.Compress(input, output);
```

`LzipOptions.Level` (0..9) selects lzip's dictionary size and match-length limit; `DictionarySize`
and `MatchLenLimit` override them; `MemberSize` (uncompressed bytes per member, 0 = single) enables
`-b`-style splitting. The output is standard lzip -- verified against the reference `lzip -t`/`-d`.

## Format notes

Each lzip member is a 6-byte header (`"LZIP"` magic + version + coded dictionary size) followed by an
LZMA1 stream (fixed properties `lc=3, lp=0, pb=2`) and a 20-byte trailer (CRC32 of the uncompressed
data + uncompressed size + member size). A multi-member file is simply independent members
concatenated.

## Attribution

The LZMA1 compression core (`src/Lzip.Lib/Lzma/`) is vendored from the **public-domain 7-Zip LZMA
SDK** by Igor Pavlov (<https://www.7-zip.org/sdk.html>). The only edits vs upstream are a
`#nullable disable` header and making the namespace-level types `internal` so Lzip.Lib's public API
stays `SharpAstro.Lzip.*`. The decoder and the lzip container framing (`LzipDecoder`,
`LzipEncoder`, `LzipFraming`) are original code. The lzip format itself was created by Antonio Diaz
Diaz; this library was validated for compatibility against the reference `lzip` binary but contains
no lzip (GPL) source.

## License

MIT -- see [LICENSE](LICENSE). The vendored LZMA SDK is public domain.
