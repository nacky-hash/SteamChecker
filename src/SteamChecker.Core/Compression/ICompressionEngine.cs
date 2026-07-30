namespace SteamChecker.Core.Compression;

/// <summary>WOF が対応する透過圧縮アルゴリズム。</summary>
public enum CompressionAlgorithm
{
    /// <summary>最速・最低圧縮率。</summary>
    Xpress4K,

    Xpress8K,

    Xpress16K,

    /// <summary>最高圧縮率・展開はやや重い。ゲームには通常これ。</summary>
    Lzx,
}

public static class CompressionAlgorithmExtensions
{
    public static string ToCompactArgument(this CompressionAlgorithm algorithm) => algorithm switch
    {
        CompressionAlgorithm.Xpress4K => "XPRESS4K",
        CompressionAlgorithm.Xpress8K => "XPRESS8K",
        CompressionAlgorithm.Xpress16K => "XPRESS16K",
        CompressionAlgorithm.Lzx => "LZX",
        _ => "LZX",
    };
}

public sealed record CompressionProgress
{
    public required int FilesProcessed { get; init; }

    /// <summary>直近に処理したファイルのパス（取得できた場合）。</summary>
    public string? CurrentFile { get; init; }
}

public sealed record CompressionResult
{
    public required bool Success { get; init; }

    public required string Path { get; init; }

    /// <summary>操作前の実占有バイト数。</summary>
    public required long BytesBefore { get; init; }

    /// <summary>操作後の実占有バイト数。</summary>
    public required long BytesAfter { get; init; }

    public required int FilesProcessed { get; init; }

    public required TimeSpan Duration { get; init; }

    public string? ErrorMessage { get; init; }

    /// <summary>dry-run（実書き込みなし）の結果か。表示側は「完了」と偽らないこと。</summary>
    public bool WasDryRun { get; init; }

    public long BytesSaved => BytesBefore - BytesAfter;
}

public interface ICompressionEngine
{
    /// <summary>この環境で圧縮を実行できるか（Windows かつ compact.exe が存在するか）。</summary>
    bool IsAvailable { get; }

    Task<CompressionResult> CompressAsync(
        string folderPath,
        CompressionAlgorithm algorithm,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken ct = default);

    Task<CompressionResult> DecompressAsync(
        string folderPath,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken ct = default);
}
