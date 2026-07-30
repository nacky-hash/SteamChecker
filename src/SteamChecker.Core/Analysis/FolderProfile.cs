namespace SteamChecker.Core.Analysis;

/// <summary>ゲームフォルダを走査して得た構成情報。</summary>
public sealed record FolderProfile
{
    public required string Path { get; init; }

    /// <summary>論理サイズ合計（バイト）。</summary>
    public required long TotalLogicalBytes { get; init; }

    /// <summary>ディスク上の実占有サイズ合計（バイト）。既に圧縮済みなら論理サイズより小さい。</summary>
    public required long TotalPhysicalBytes { get; init; }

    public required int FileCount { get; init; }

    /// <summary>カテゴリ別の論理バイト数。</summary>
    public required IReadOnlyDictionary<FileCategory, long> BytesByCategory { get; init; }

    /// <summary>検出した特徴（DirectStorage / アンチチート / 圧縮済み）。</summary>
    public GameFeatures Features { get; init; } = GameFeatures.None;

    /// <summary>走査が途中で打ち切られたか（ファイル数上限に達した等）。</summary>
    public bool Truncated { get; init; }

    /// <summary>
    /// 実占有 / 論理 の比。1.0 に近ければ未圧縮、小さければ既に圧縮が効いている。
    /// </summary>
    public double PhysicalToLogicalRatio =>
        TotalLogicalBytes > 0 ? (double)TotalPhysicalBytes / TotalLogicalBytes : 1.0;

    /// <summary>拡張子ヒューリスティックのみによる「圧縮後サイズ / 元サイズ」の事前推定。</summary>
    public double HeuristicRatio()
    {
        if (TotalLogicalBytes <= 0) return 1.0;

        double weighted = 0;
        foreach (var (category, bytes) in BytesByCategory)
        {
            weighted += FileCategories.PriorRatio(category) * bytes;
        }

        return Math.Clamp(weighted / TotalLogicalBytes, 0.0, 1.0);
    }

    /// <summary>指定カテゴリが全体に占める割合。</summary>
    public double ShareOf(FileCategory category)
    {
        if (TotalLogicalBytes <= 0) return 0;
        return BytesByCategory.TryGetValue(category, out var b) ? (double)b / TotalLogicalBytes : 0;
    }
}

/// <summary>
/// ゲームフォルダを 1 パスで走査し、サイズ構成と特徴フラグを同時に集計する。
/// 100GB 級のフォルダを何度も歩き直さないよう、意図的に 1 パスに統合している。
/// </summary>
public sealed class FolderProfiler(IFileSystem fs, int maxFiles = 250_000)
{
    private readonly IFileSystem _fs = fs;
    private readonly int _maxFiles = maxFiles;

    /// <summary>圧縮済みと判定する閾値。実占有が論理の 90% 未満なら何らかの圧縮が効いている。</summary>
    private const double AlreadyCompressedThreshold = 0.90;

    public FolderProfile Profile(string path, CancellationToken ct = default)
    {
        var bytesByCategory = new Dictionary<FileCategory, long>();
        var detector = new FeatureDetector();
        long totalLogical = 0;
        long totalPhysical = 0;
        var count = 0;
        var truncated = false;

        foreach (var file in _fs.EnumerateFilesRecursive(path))
        {
            ct.ThrowIfCancellationRequested();

            if (count >= _maxFiles)
            {
                truncated = true;
                break;
            }

            count++;
            detector.Feed(_fs.GetFileName(file));

            var size = _fs.GetFileSize(file);
            if (size <= 0) continue;

            var category = FileCategories.FromExtension(_fs.GetExtension(file));

            bytesByCategory.TryGetValue(category, out var existing);
            bytesByCategory[category] = existing + size;

            totalLogical += size;
            totalPhysical += _fs.GetFileSizeOnDisk(file);
        }

        if (totalLogical > 0 && (double)totalPhysical / totalLogical < AlreadyCompressedThreshold)
        {
            detector.MarkAlreadyCompressed();
        }

        return new FolderProfile
        {
            Path = path,
            TotalLogicalBytes = totalLogical,
            TotalPhysicalBytes = totalPhysical,
            FileCount = count,
            BytesByCategory = bytesByCategory,
            Features = detector.Result,
            Truncated = truncated,
        };
    }
}
