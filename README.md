# Lzip.Lib

Pure-managed **lzip** (LZMA1) codec for .NET -- a fast, AOT/trim-friendly decompressor for the
[lzip](https://www.nongnu.org/lzip/) container format, including multi-member files that are decoded
in parallel. No external `lzip` binary required.

`1.0` ships the **decoder**. An **encoder** (LZMA1 + lzip member framing + `-b`-style chunking) is
planned for a later release.

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

## Format notes

Each lzip member is a 6-byte header (`"LZIP"` magic + version + coded dictionary size) followed by an
LZMA1 stream (fixed properties `lc=3, lp=0, pb=2`) and a 20-byte trailer (CRC32 of the uncompressed
data + uncompressed size + member size). A multi-member file is simply independent members
concatenated.

## License

MIT -- see [LICENSE](LICENSE).
