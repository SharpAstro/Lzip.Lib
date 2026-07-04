namespace SharpAstro.Lzip;

/// <summary>
/// Compression options for <see cref="LzipEncoder"/>. Defaults approximate <c>lzip -9</c>
/// (best ratio, single member).
/// </summary>
public sealed class LzipOptions
{
    /// <summary>
    /// Compression level 0..9 (lzip <c>-0</c>..<c>-9</c>). Selects the dictionary size and match-length
    /// limit from lzip's level table unless <see cref="DictionarySize"/> / <see cref="MatchLenLimit"/>
    /// override them. Default <c>9</c>.
    /// </summary>
    public int Level { get; init; } = 9;

    /// <summary>
    /// Explicit dictionary size in bytes (4 KiB .. 512 MiB), rounded up to the nearest
    /// lzip-representable size. <c>null</c> derives it from <see cref="Level"/> and caps it to the
    /// input size (as lzip does), so small inputs get a small dictionary.
    /// </summary>
    public int? DictionarySize { get; init; }

    /// <summary>
    /// Explicit match-length limit (5..273, lzip's "fast bytes"). <c>null</c> derives it from
    /// <see cref="Level"/>.
    /// </summary>
    public int? MatchLenLimit { get; init; }

    /// <summary>
    /// Uncompressed bytes per member. <c>0</c> (default) emits a single member. A positive value
    /// splits the output into independent members of about this size -- like <c>lzip -b</c> --
    /// which <see cref="LzipDecoder"/> decodes in parallel.
    /// </summary>
    public long MemberSize { get; init; }

    /// <summary>Shared default instance (<see cref="Level"/> 9, single member).</summary>
    public static LzipOptions Default { get; } = new LzipOptions();
}
