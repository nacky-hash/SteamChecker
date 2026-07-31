using SteamChecker.Core.Analysis;
using SteamChecker.Core.Steam;

namespace SteamChecker.Core;

public sealed record ScanProgress
{
    public required int Completed { get; init; }

    public required int Total { get; init; }

    public string? CurrentTitle { get; init; }

    /// <summary>解析を終えたタイトルの論理サイズ合計。</summary>
    public long CompletedBytes { get; init; }

    /// <summary>解析対象の論理サイズ合計。</summary>
    public long TotalBytes { get; init; }

    /// <summary>
    /// 進捗率（0.0〜1.0）。件数ではなくバイト数で測る。
    ///
    /// 1 タイトルあたりの所要時間は容量にほぼ比例し、
    /// 117GB のタイトルと 0.2GB のタイトルでは数百倍違う。
    /// 件数で測ったゲージは「44 件中 40 件終わったのに残り時間が減らない」という
    /// 嘘をつくことになる。
    /// </summary>
    public double Fraction => TotalBytes > 0
        ? Math.Clamp((double)CompletedBytes / TotalBytes, 0.0, 1.0)
        : (Total > 0 ? Math.Clamp((double)Completed / Total, 0.0, 1.0) : 0.0);
}

public sealed record ScanResult
{
    public required IReadOnlyList<GameAssessment> Assessments { get; init; }

    public required IReadOnlyList<SteamLibrary> Libraries { get; init; }

    public required IReadOnlyList<SteamUser> Users { get; init; }

    /// <summary>プレイ履歴が 1 件も取れなかった場合 true。判定の信頼度が落ちるので UI で明示すべき。</summary>
    public required bool PlayHistoryUnavailable { get; init; }

    public long TotalSizeBytes => Assessments.Sum(a => a.SizeBytes);

    public long TotalEstimatedSavingBytes => Assessments
        .Where(a => a.Advice is AdviceKind.Compress
                              or AdviceKind.CompressUpdatesOften
                              or AdviceKind.CompressAntiCheat)
        .Sum(a => a.Estimate.EstimatedSavedBytes);

    /// <summary>削除候補（事実として提示するのみ。ツールは削除しない）の合計サイズ。</summary>
    public long UninstallCandidateBytes => Assessments
        .Where(a => a.IsUninstallCandidate)
        .Sum(a => a.SizeBytes);
}

/// <summary>
/// 解析前のタイトル情報。manifest と localconfig を読むだけで得られる範囲。
///
/// 圧縮見込みの推定は 100GB 級のフォルダを走査するため数分かかる。
/// それを待たずに「何が入っていて、いつ遊んで、どれくらい大きいか」だけを
/// 即座に表示するために分けている（`ReadTitles` は実測で 50〜400ms）。
/// </summary>
public sealed record TitleSummary
{
    public required long AppId { get; init; }

    public required string Name { get; init; }

    public required string InstallPath { get; init; }

    /// <summary>Steam が manifest で報告するサイズ。実測ではない。</summary>
    public required long SizeBytes { get; init; }

    public DateTimeOffset? LastPlayed { get; init; }

    /// <summary>未起動日数。起動記録が無ければ null（「一度も遊んでいない」とは断定しない）。</summary>
    public int? DaysSincePlayed { get; init; }

    public bool IsFullyInstalled { get; init; }
}

/// <summary>
/// Steam ライブラリ全体を走査して、タイトルごとの推奨を組み立てる。
/// このクラスは一切書き込みを行わない。Phase 0（解析のみ）はこれだけで成立する。
/// </summary>
public sealed class LibraryScanner(
    IFileSystem fs,
    AdvisorOptions? advisorOptions = null,
    SamplingOptions? samplingOptions = null,
    ICompressibilityProbe? probe = null,
    TimeProvider? timeProvider = null)
{
    private readonly IFileSystem _fs = fs;
    private readonly SteamReader _reader = new(fs);
    private readonly FolderProfiler _profiler = new(fs);
    private readonly SamplingEstimator _estimator = new(fs, probe, samplingOptions);
    private readonly Advisor _advisor = new(advisorOptions, timeProvider);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// インストール済みタイトルの一覧だけを返す。フォルダ走査もサンプリングも行わない。
    /// UI が起動直後に「空の画面」を見せないためのもの。
    /// </summary>
    public IReadOnlyList<TitleSummary> ReadTitles(string steamRoot, CancellationToken ct = default)
    {
        var libraries = _reader.ReadLibraries(steamRoot);
        var users = _reader.ReadUsers(steamRoot);
        var playRecords = _reader.ReadMergedPlayRecords(users);
        var now = _time.GetUtcNow();

        var result = new List<TitleSummary>();

        foreach (var app in libraries.SelectMany(_reader.ReadInstalledApps))
        {
            ct.ThrowIfCancellationRequested();

            if (!_fs.DirectoryExists(app.FullPath)) continue;

            playRecords.TryGetValue(app.AppId, out var play);

            result.Add(new TitleSummary
            {
                AppId = app.AppId,
                Name = app.Name,
                InstallPath = app.FullPath,
                SizeBytes = app.SizeOnDisk,
                LastPlayed = play?.LastPlayed,
                DaysSincePlayed = play?.LastPlayed is { } lp ? (int)(now - lp).TotalDays : null,
                IsFullyInstalled = app.IsFullyInstalled,
            });
        }

        return result.OrderByDescending(t => t.SizeBytes).ToList();
    }

    /// <param name="assumeNtfs">
    /// ファイルシステム判定を飛ばして NTFS とみなす。
    /// Windows 以外で判定ロジックを動作確認するための開発用オプション。
    /// </param>
    public ScanResult Scan(
        string steamRoot,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default,
        bool assumeNtfs = false)
    {
        var libraries = _reader.ReadLibraries(steamRoot);
        var users = _reader.ReadUsers(steamRoot);
        var playRecords = _reader.ReadMergedPlayRecords(users);

        var apps = libraries.SelectMany(_reader.ReadInstalledApps).ToList();

        // ファイルシステム名はドライブごとに 1 回だけ判定する（DriveInfo は安くない）
        var ntfsCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        var assessments = new List<GameAssessment>(apps.Count);
        var completed = 0;

        // 進捗はバイト数で測る。所要時間は容量にほぼ比例するため、
        // 件数ベースのゲージは実態とかけ離れる（ScanProgress.Fraction を参照）
        var totalBytes = apps.Sum(a => a.SizeOnDisk);
        long completedBytes = 0;

        foreach (var app in apps)
        {
            ct.ThrowIfCancellationRequested();

            progress?.Report(new ScanProgress
            {
                Completed = completed,
                Total = apps.Count,
                CurrentTitle = app.Name,
                CompletedBytes = completedBytes,
                TotalBytes = totalBytes,
            });

            completed++;
            completedBytes += app.SizeOnDisk;

            if (!_fs.DirectoryExists(app.FullPath)) continue;

            var profile = _profiler.Profile(app.FullPath, ct);
            var estimate = _estimator.Estimate(profile, ct);
            playRecords.TryGetValue(app.AppId, out var play);

            assessments.Add(_advisor.Assess(app, profile, estimate, play, IsNtfs(app.FullPath)));
        }

        progress?.Report(new ScanProgress
        {
            Completed = completed,
            Total = apps.Count,
            CompletedBytes = totalBytes,
            TotalBytes = totalBytes,
        });

        return new ScanResult
        {
            Assessments = assessments
                .OrderByDescending(a => a.Estimate.EstimatedSavedBytes)
                .ToList(),
            Libraries = libraries,
            Users = users,
            PlayHistoryUnavailable = playRecords.Count == 0,
        };

        bool IsNtfs(string path)
        {
            if (assumeNtfs) return true;

            var root = _fs.GetVolumeRoot(path) ?? path;

            if (ntfsCache.TryGetValue(root, out var cached)) return cached;

            var name = _fs.GetFileSystemName(path);

            // 判定できない場合は「NTFS である」と仮定する。
            // ここで false にすると全タイトルが「圧縮非推奨」になり、
            // 判定不能を「非対応」と誤って伝えてしまうため。
            var isNtfs = name is null
                         || name.Equals("NTFS", StringComparison.OrdinalIgnoreCase);

            ntfsCache[root] = isNtfs;
            return isNtfs;
        }
    }
}
