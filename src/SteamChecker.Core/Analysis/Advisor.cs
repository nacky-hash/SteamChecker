using SteamChecker.Core.Steam;

namespace SteamChecker.Core.Analysis;

public sealed record AdvisorOptions
{
    /// <summary>この割合未満しか縮まないなら「圧縮しても無駄」。</summary>
    public double MinSavedFraction { get; init; } = 0.10;

    /// <summary>この量未満しか空かないなら「圧縮しても無駄」。既定 1GiB。</summary>
    public long MinSavedBytes { get; init; } = 1L * 1024 * 1024 * 1024;

    /// <summary>この日数以内に更新されていれば「更新が活発」とみなす。</summary>
    public int RecentUpdateDays { get; init; } = 30;

    /// <summary>この日数以上プレイしていなければ「長期未起動」。</summary>
    public int LongUnplayedDays { get; init; } = 180;

    /// <summary>削除検討の対象にする最小サイズ。既定 20GiB。</summary>
    public long UninstallMinBytes { get; init; } = 20L * 1024 * 1024 * 1024;

    /// <summary>削除が圧縮の何倍空くなら「削除のほうが得」と言うか。</summary>
    public double DeleteAdvantageMultiple { get; init; } = 4.0;

    /// <summary>「大きな削減」と表示する閾値。既定 5GiB。</summary>
    public long LargeSavingBytes { get; init; } = 5L * 1024 * 1024 * 1024;

    public UninstallAdviceMode UninstallAdvice { get; init; } = UninstallAdviceMode.Subtle;
}

/// <summary>
/// 判定エンジン。本ツールの独自価値はここに集約される。
///
/// 既存の圧縮ツールは「ユーザーが選んだフォルダを圧縮する道具」であり、
/// どれをどうすべきかは全てユーザー任せになっている。
/// このクラスは プレイ履歴 × 更新頻度 × 実測圧縮率 × サイズ × 技術的制約 を
/// 突き合わせて、タイトルごとに 1 つの推奨と、その根拠を返す。
/// </summary>
public sealed class Advisor(AdvisorOptions? options = null, TimeProvider? timeProvider = null)
{
    private readonly AdvisorOptions _options = options ?? new AdvisorOptions();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public GameAssessment Assess(
        InstalledApp app,
        FolderProfile profile,
        CompressionEstimate estimate,
        PlayRecord? play,
        bool isNtfs)
    {
        var now = _time.GetUtcNow();
        var reasons = new List<ReasonCode>();

        var daysSincePlayed = play?.LastPlayed is { } lp
            ? (int)(now - lp).TotalDays
            : (int?)null;

        var daysSinceUpdated = app.LastUpdated is { } lu
            ? (int)(now - lu).TotalDays
            : (int?)null;

        var size = profile.TotalLogicalBytes > 0 ? profile.TotalLogicalBytes : app.SizeOnDisk;
        var savedBytes = estimate.EstimatedSavedBytes;
        var savedFraction = estimate.SavedFraction;

        if (profile.Truncated) reasons.Add(ReasonCode.ScanTruncated);
        if (!app.IsFullyInstalled) reasons.Add(ReasonCode.NotFullyInstalled);

        // --- プレイ履歴に関する事実を先に積む（推奨の分岐に関わらず表示したい） ---
        if (play is null || play.NeverPlayed)
        {
            reasons.Add(ReasonCode.NeverPlayed);
        }
        else if (daysSincePlayed >= _options.LongUnplayedDays)
        {
            reasons.Add(ReasonCode.LongUnplayed);
        }
        else
        {
            reasons.Add(ReasonCode.RecentlyPlayed);
        }

        // --- 削除のほうが得か（事実。削除は実行しない） ---
        var deleteMultiple = savedBytes > 0 ? (double)size / savedBytes : double.PositiveInfinity;
        var longUnplayed = play is null || play.NeverPlayed || daysSincePlayed >= _options.LongUnplayedDays;

        var uninstallCandidate =
            _options.UninstallAdvice != UninstallAdviceMode.Off
            && longUnplayed
            && size >= _options.UninstallMinBytes
            && deleteMultiple >= _options.DeleteAdvantageMultiple;

        if (uninstallCandidate) reasons.Add(ReasonCode.DeleteYieldsMuchMore);

        // --- 技術的に圧縮できない / すべきでないケースを先に弾く ---

        // ダウンロード中・更新中に compact を当てると Steam の書き込みと競合する。
        // 理由コードを積むだけでは「圧縮推奨」まで素通りしてしまうので、ここで確実に止める
        if (!app.IsFullyInstalled)
        {
            return Build(AdviceKind.DoNotCompress);
        }

        if (!isNtfs)
        {
            reasons.Add(ReasonCode.NotNtfs);
            return Build(AdviceKind.DoNotCompress);
        }

        if (profile.Features.HasFlag(GameFeatures.DirectStorage))
        {
            reasons.Add(ReasonCode.DirectStorageDetected);
            return Build(AdviceKind.DoNotCompress);
        }

        if (profile.Features.HasFlag(GameFeatures.AlreadyCompressed))
        {
            reasons.Add(ReasonCode.AlreadyCompressed);
            return Build(AdviceKind.AlreadyCompressed);
        }

        // --- 効果が小さいケース ---
        if (savedFraction < _options.MinSavedFraction)
        {
            reasons.Add(ReasonCode.LowCompressibility);
            return Build(AdviceKind.NotWorthCompressing);
        }

        if (savedBytes < _options.MinSavedBytes)
        {
            reasons.Add(ReasonCode.SavingTooSmall);
            return Build(AdviceKind.NotWorthCompressing);
        }

        if (savedBytes >= _options.LargeSavingBytes) reasons.Add(ReasonCode.LargeSaving);

        // --- アンチチート検出時は必ずユーザーの明示的な選択を挟む ---
        if (profile.Features.HasAnyAntiCheat())
        {
            reasons.Add(ReasonCode.AntiCheatDetected);
            return Build(AdviceKind.CompressAntiCheat);
        }

        // --- 更新頻度で圧縮の持続性が変わる ---
        if (daysSinceUpdated is { } d && d <= _options.RecentUpdateDays)
        {
            reasons.Add(ReasonCode.RecentlyUpdated);
            return Build(AdviceKind.CompressUpdatesOften);
        }

        reasons.Add(ReasonCode.UpdateStable);
        return Build(AdviceKind.Compress);

        GameAssessment Build(AdviceKind kind) => new()
        {
            AppId = app.AppId,
            Name = app.Name,
            InstallPath = app.FullPath,
            Advice = kind,
            Reasons = reasons,
            SizeBytes = size,
            PhysicalBytes = profile.TotalPhysicalBytes,
            Estimate = estimate,
            LastPlayed = play?.LastPlayed,
            DaysSincePlayed = daysSincePlayed,
            DaysSinceUpdated = daysSinceUpdated,
            Features = profile.Features,
            IsUninstallCandidate = uninstallCandidate,
        };
    }
}
