namespace SteamChecker.Core.Analysis;

/// <summary>圧縮見込みの推定結果。</summary>
public sealed record CompressionEstimate
{
    /// <summary>圧縮後サイズ / 元サイズ の推定値。</summary>
    public required double Ratio { get; init; }

    /// <summary>推定される削減バイト数。</summary>
    public required long EstimatedSavedBytes { get; init; }

    /// <summary>実測サンプリングに基づくか（false ならヒューリスティックのみ）。</summary>
    public required bool Measured { get; init; }

    /// <summary>実際に読んだサンプルのバイト数。</summary>
    public long SampledBytes { get; init; }

    /// <summary>サンプリングしたファイル数。</summary>
    public int SampledFiles { get; init; }

    /// <summary>カテゴリ別の実測比率（サンプリングできたものだけ）。</summary>
    public IReadOnlyDictionary<FileCategory, double> MeasuredByCategory { get; init; }
        = new Dictionary<FileCategory, double>();

    /// <summary>削減率（0.0〜1.0）。</summary>
    public double SavedFraction => 1.0 - Ratio;
}

/// <summary>
/// フォルダから層化サンプルを読み、実測して圧縮率を外挿する。
///
/// CompactGUI はコミュニティ製の 10 万件規模データベースで圧縮率を予測している。
/// 新規ツールが同じ土俵に乗ってもユーザー数が無いのでデータベースは育たず、
/// 見積もりを外して信頼を失う。この実装はデータベースもネットワークも使わず、
/// 対象マシンの実データを読んで測ることでその不利を回避する。
/// </summary>
public sealed class SamplingEstimator(
    IFileSystem fs,
    ICompressibilityProbe? probe = null,
    SamplingOptions? options = null)
{
    private readonly IFileSystem _fs = fs;
    private readonly ICompressibilityProbe _probe = probe ?? new ChunkedBrotliProbe();
    private readonly SamplingOptions _options = options ?? new SamplingOptions();

    public CompressionEstimate Estimate(FolderProfile profile, CancellationToken ct = default)
    {
        if (profile.TotalLogicalBytes <= 0)
        {
            return new CompressionEstimate
            {
                Ratio = 1.0,
                EstimatedSavedBytes = 0,
                Measured = false,
            };
        }

        // 1. 対象ファイルをカテゴリ別に集める
        var byCategory = CollectCandidates(profile.Path, ct);

        // フォルダが大きいほどサンプルも増やす（実測根拠: docs/RESEARCH.md §6）
        var totalBudget = Math.Clamp(
            profile.TotalLogicalBytes / Math.Max(1, _options.AdaptiveBudgetDivisor),
            _options.TotalSampleBudgetBytes,
            _options.MaxSampleBudgetBytes);

        // 2. カテゴリごとにバイト数比例でサンプル予算を配分し、実測する
        var measured = new Dictionary<FileCategory, double>();
        long sampledBytes = 0;
        var sampledFiles = 0;

        foreach (var (category, files) in byCategory)
        {
            ct.ThrowIfCancellationRequested();

            // 実測を省いてよいのは「定義上すでに圧縮済み」のものだけ。
            //
            // かつてここで FileCategory.Archive（.arc / .rpf / .vpk 等を含む）も
            // まとめて省いていたが、それは誤りだった。
            // theHunter: Call of the Wild の .arc は実測 LZX 29.4% で、
            // 117GB のうち約 82GB が削減できる。事前値 0.98 で決め打つと
            // 「2.38GB しか縮まない」と表示し、34 倍過小に見積もっていた。
            //
            // 「拡張子では中身が圧縮済みか判断できない」というのがこのツールの前提であり、
            // 名前がアーカイブというだけで実測を省くのはその前提と矛盾する。
            if (category is FileCategory.CompressedMedia or FileCategory.CompressionFormat) continue;

            var categoryBytes = profile.BytesByCategory.TryGetValue(category, out var b) ? b : 0;
            if (categoryBytes <= 0) continue;

            var share = (double)categoryBytes / profile.TotalLogicalBytes;
            var budget = (long)(totalBudget * share);
            budget = Math.Max(budget, _options.MinPerCategoryBytes);

            // カテゴリ内を拡張子でさらに層化する。
            // 同じ Unknown でも .ress（よく縮む）と .bank（縮まない）が混在し、
            // 読んだバイト数での平均だと少数の大ファイル群の重みを取り違える。
            // 拡張子ごとのバイト比は列挙で正確に分かっているので、
            // 「拡張子内は等重み平均、拡張子間は正確なバイト比」で合成する。
            // 実例: Slots & Daggers で .bank (42MB, 実測0.98) の取りこぼしにより
            // 削減見込みを 11pt 過大に出した（docs/RESEARCH.md §6）
            var byExtension = files
                .GroupBy(f => _fs.GetExtension(f.Path), StringComparer.OrdinalIgnoreCase)
                .ToList();

            var candidateBytes = files.Sum(f => f.Size);
            if (candidateBytes <= 0) continue;

            double extWeightedRatio = 0;
            long extMeasuredBytes = 0;

            foreach (var group in byExtension)
            {
                ct.ThrowIfCancellationRequested();

                var groupFiles = group.ToList();
                var groupBytes = groupFiles.Sum(f => f.Size);
                var groupBudget = Math.Max(
                    (long)(budget * ((double)groupBytes / candidateBytes)),
                    _options.BreadthBytesPerFile);

                var result = MeasureCategory(groupFiles, groupBudget, ct);
                if (result is null) continue;

                extWeightedRatio += result.Value.Ratio * groupBytes;
                extMeasuredBytes += groupBytes;
                sampledBytes += result.Value.Bytes;
                sampledFiles += result.Value.Files;
            }

            if (extMeasuredBytes <= 0) continue;

            measured[category] = extWeightedRatio / extMeasuredBytes;
        }

        // 3. 全体比率を合成する。実測できたカテゴリは実測値、できなければ事前値
        double weightedRatio = 0;
        foreach (var (category, bytes) in profile.BytesByCategory)
        {
            var ratio = measured.TryGetValue(category, out var m)
                ? m
                : FileCategories.PriorRatio(category);

            weightedRatio += ratio * bytes;
        }

        var finalRatio = Math.Clamp(weightedRatio / profile.TotalLogicalBytes, 0.0, 1.0);

        return new CompressionEstimate
        {
            Ratio = finalRatio,
            EstimatedSavedBytes = (long)(profile.TotalLogicalBytes * (1.0 - finalRatio)),
            Measured = measured.Count > 0,
            SampledBytes = sampledBytes,
            SampledFiles = sampledFiles,
            MeasuredByCategory = measured,
        };
    }

    private Dictionary<FileCategory, List<(string Path, long Size)>> CollectCandidates(
        string root, CancellationToken ct)
    {
        var result = new Dictionary<FileCategory, List<(string, long)>>();
        var seen = 0;

        foreach (var file in _fs.EnumerateFilesRecursive(root))
        {
            ct.ThrowIfCancellationRequested();

            if (++seen > _options.MaxFilesToConsider) break;

            var size = _fs.GetFileSize(file);
            if (size < _options.MinFileSizeBytes) continue;

            var category = FileCategories.FromExtension(_fs.GetExtension(file));

            if (!result.TryGetValue(category, out var list))
            {
                list = [];
                result[category] = list;
            }

            list.Add((file, size));
        }

        return result;
    }

    private (double Ratio, long Bytes, int Files)? MeasureCategory(
        List<(string Path, long Size)> files, long budget, CancellationToken ct)
    {
        if (files.Count == 0) return null;

        // 決定的サンプリング: パスのハッシュで並べ替える。
        // ランダムだと実行のたびに数字が変わり「さっきと違う」と信頼を失うため。
        var ordered = files
            .OrderBy(f => StableHash(f.Path))
            .ToList();

        // 1. サンプル対象を先に確定する。
        //    並列化しても「どのファイルを読むか」が実行のたびに変わらないようにするため、
        //    選択は読み込みと分離して決定論的に行う。
        //
        //    配分は2段階。まず「広く浅く」（各ファイルから少量ずつ）、
        //    残った予算で「深く」（ファイルあたり上限まで追加）。
        //    深掘りだけだと、大ファイル数本で予算を使い切り、同カテゴリ内の
        //    別グループを一度も見ないことがある。実例: Slots & Daggers では
        //    Unknown 予算を .ress/.assets で消費し、非圧縮性の .bank (42MB) を
        //    1本もサンプルせず、削減見込みを 11pt 過大に出した（docs/RESEARCH.md §6）
        var candidates = ordered.Take(_options.MaxFilesPerCategory).ToList();
        var planned = new long[candidates.Count];
        long plannedBytes = 0;

        // 第1段: 広く浅く
        for (var i = 0; i < candidates.Count && plannedBytes < budget; i++)
        {
            var take = Math.Min(candidates[i].Size, _options.BreadthBytesPerFile);
            planned[i] = take;
            plannedBytes += take;
        }

        // 第2段: 残予算で深く（同じハッシュ順）
        for (var i = 0; i < candidates.Count && plannedBytes < budget; i++)
        {
            var cap = Math.Min(candidates[i].Size, _options.MaxBytesPerFile);
            var extra = Math.Min(cap - planned[i], budget - plannedBytes);
            if (extra <= 0) continue;

            planned[i] += extra;
            plannedBytes += extra;
        }

        var selection = new List<(string Path, long Size, int PlannedRead)>();
        for (var i = 0; i < candidates.Count; i++)
        {
            if (planned[i] > 0) selection.Add((candidates[i].Path, candidates[i].Size, (int)planned[i]));
        }

        if (selection.Count == 0) return null;

        // 2. 確定した対象を並列に実測する。
        //    Brotli の計算が支配的で、合計は可換なので結果は順序に依存しない。
        //    合成は「読んだバイト数」加重（≒ファイル間の等重みに近い）。
        //    ファイルの論理サイズで加重する案も実測で比較したが、
        //    少数の巨大ファイルの個体差が全体を支配して分散が増え、
        //    既定予算では誤差がむしろ悪化した（docs/RESEARCH.md §6）
        long totalOriginal = 0;
        long totalCompressed = 0;
        var fileCount = 0;

        Parallel.ForEach(
            selection,
            new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = Math.Max(1, _options.MaxParallelism),
            },
            () => new byte[_options.MaxBytesPerFile],
            (item, _, buffer) =>
            {
                var read = ReadSample(item.Path, item.Size, buffer, item.PlannedRead);
                if (read > 0)
                {
                    var ratio = _probe.Measure(buffer.AsSpan(0, read));

                    Interlocked.Add(ref totalOriginal, read);
                    Interlocked.Add(ref totalCompressed, (long)(read * ratio));
                    Interlocked.Increment(ref fileCount);
                }

                return buffer;
            },
            _ => { });

        if (totalOriginal <= 0) return null;

        return ((double)totalCompressed / totalOriginal, totalOriginal, fileCount);
    }

    /// <summary>
    /// ファイルからサンプルを読む。大きいファイルは先頭だけだと
    /// ヘッダやパディングに引きずられるため、複数箇所から均等に読む。
    /// </summary>
    private int ReadSample(string path, long reportedSize, byte[] buffer, int? maxRead = null)
    {
        try
        {
            using var stream = _fs.OpenRead(path);
            if (stream is null) return 0;

            // ディレクトリ列挙が返したサイズを信用しない。
            // 走査中にゲームが更新されればファイルは縮みうるし、
            // スパースファイルや仮想ファイルでは報告値と実体が食い違う。
            // 報告値を信じて Seek すると範囲外例外でスキャン全体が落ちる。
            var actualSize = reportedSize;
            if (stream.CanSeek && stream.Length > 0)
            {
                actualSize = Math.Min(reportedSize, stream.Length);
            }

            var limit = Math.Min(buffer.Length, maxRead ?? buffer.Length);
            var target = (int)Math.Min(actualSize, limit);
            if (target <= 0) return 0;

            // 全部読める大きさなら丸ごと読む
            if (actualSize <= target || !stream.CanSeek)
            {
                return ReadFully(stream, buffer, 0, target);
            }

            // 大きいファイルは複数箇所から等分に読む。
            // 先頭だけだとヘッダやパディングに引きずられて実態とずれる。
            // ゲームの独自コンテナは内部で領域ごとに性質が変わる
            // （圧縮済みテクスチャ区画と生データ区画が混在する）ため、
            // 箇所数を増やすほど within-file の分散が下がる（docs/RESEARCH.md §6）
            const int slices = 8;
            var perSlice = target / slices;
            if (perSlice <= 0) return ReadFully(stream, buffer, 0, target);

            var written = 0;

            for (var i = 0; i < slices; i++)
            {
                // 25% / 50% / 75% 付近から
                var position = (long)(actualSize * (i + 1) / (double)(slices + 1));
                position = Math.Clamp(position, 0, Math.Max(0, actualSize - perSlice));

                stream.Seek(position, SeekOrigin.Begin);
                var n = ReadFully(stream, buffer, written, perSlice);
                if (n <= 0) break;
                written += n;
            }

            return written;
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or ArgumentOutOfRangeException
                                      or NotSupportedException
                                      or ObjectDisposedException)
        {
            // 1 ファイル読めなかっただけでライブラリ全体の走査を止めない
            return 0;
        }
    }

    private static int ReadFully(Stream stream, byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (total < count)
        {
            var n = stream.Read(buffer, offset + total, count - total);
            if (n <= 0) break;
            total += n;
        }

        return total;
    }

    private static uint StableHash(string value)
    {
        // FNV-1a。実行間で安定していればよく、暗号強度は不要
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in value)
            {
                hash ^= c;
                hash *= 16777619u;
            }

            return hash;
        }
    }
}

public sealed record SamplingOptions
{
    /// <summary>1 フォルダあたりに読むサンプルの最低総量。</summary>
    public long TotalSampleBudgetBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>
    /// 適応予算の分母。実サンプル量は logical/この値 を
    /// [TotalSampleBudgetBytes, MaxSampleBudgetBytes] に丸めたもの。
    ///
    /// 64MB 固定だった頃の実測（2026-07-30、docs/RESEARCH.md §6）では、
    /// 異種混合の実ゲームフォルダで誤差が最大 7.6pt に達した。
    /// サンプル量を増やすと全タイトルが 3pt 以内に収束したため、
    /// フォルダが大きいほどサンプルも増やす（logical の約 6%、上限あり）。
    /// </summary>
    public long AdaptiveBudgetDivisor { get; init; } = 16;

    /// <summary>適応予算の上限。走査時間の上限を抑える。</summary>
    public long MaxSampleBudgetBytes { get; init; } = 1024L * 1024 * 1024;

    /// <summary>カテゴリごとの最低サンプル量。</summary>
    public long MinPerCategoryBytes { get; init; } = 2L * 1024 * 1024;

    /// <summary>1 ファイルから読む最大バイト数。</summary>
    public int MaxBytesPerFile { get; init; } = 16 * 1024 * 1024;

    /// <summary>カテゴリごとの最大サンプルファイル数。</summary>
    public int MaxFilesPerCategory { get; init; } = 256;

    /// <summary>これより小さいファイルはサンプル対象にしない（統計的に無意味なうえ、WOF の恩恵も薄い）。</summary>
    public long MinFileSizeBytes { get; init; } = 64 * 1024;

    /// <summary>候補収集時に見るファイル数の上限。巨大フォルダで列挙に時間をかけすぎない。</summary>
    public int MaxFilesToConsider { get; init; } = 50_000;

    /// <summary>実測の並列度。サンプル対象の選択は並列度に依存しない（決定論を保つ）。</summary>
    public int MaxParallelism { get; init; } = Math.Min(Environment.ProcessorCount, 8);

    /// <summary>
    /// 第1段（広く浅く）で1ファイルから読む量。
    /// 深掘りだけだと大ファイル数本で予算が尽き、カテゴリ内の別グループを
    /// 一度も見ない事故が起きる（docs/RESEARCH.md §6 の Slots &amp; Daggers 事例）。
    /// </summary>
    public long BreadthBytesPerFile { get; init; } = 1024 * 1024;
}
