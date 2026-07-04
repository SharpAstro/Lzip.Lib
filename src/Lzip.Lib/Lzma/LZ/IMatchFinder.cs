// Vendored from the public-domain 7-Zip LZMA SDK (v24.09 / lzma2600), by Igor Pavlov.
// The SDK is public domain; see README "Attribution". Only two edits vs upstream:
//   1. this header + `#nullable disable` (the SDK predates nullable reference types);
//   2. namespace-level `public` types made `internal` so Lzip.Lib's public API stays
//      SharpAstro.Lzip.* (the SDK encoder is driven internally by LzipEncoder).
#nullable disable
// IMatchFinder.cs

using System;

namespace SevenZip.Compression.LZ
{
	interface IInWindowStream
	{
		void SetStream(System.IO.Stream inStream);
		void Init();
		void ReleaseStream();
		Byte GetIndexByte(Int32 index);
		UInt32 GetMatchLen(Int32 index, UInt32 distance, UInt32 limit);
		UInt32 GetNumAvailableBytes();
	}

	interface IMatchFinder : IInWindowStream
	{
		void Create(UInt32 historySize, UInt32 keepAddBufferBefore,
				UInt32 matchMaxLen, UInt32 keepAddBufferAfter);
		UInt32 GetMatches(UInt32[] distances);
		void Skip(UInt32 num);
	}
}
