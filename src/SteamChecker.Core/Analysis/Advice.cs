namespace SteamChecker.Core.Analysis;

/// <summary>1 タイトルに対する推奨アクション。</summary>
public enum AdviceKind
{
    /// <summary>圧縮推奨。効果が大きく、更新も落ち着いている。</summary>
    Compress,

    /// <summary>
    /// 圧縮できるが、更新が頻繁なので圧縮が解けやすい。
    /// WOF 圧縮はファイルへの書き込みで解除されるため、ゲーム更新のたびに効果が失われる。
    /// 常駐監視による自動再圧縮は**作らない**方針（D-018）。必要なら再実行してもらう。
    /// </summary>
    CompressUpdatesOften,

    /// <summary>圧縮可だがアンチチート検出。ユーザーの明示的な選択を必須にする。</summary>
    CompressAntiCheat,

    /// <summary>圧縮しても効果が小さい。</summary>
    NotWorthCompressing,

    /// <summary>圧縮すべきでない（DirectStorage / 非 NTFS 等）。</summary>
    DoNotCompress,

    /// <summary>既に圧縮されている。</summary>
    AlreadyCompressed,
}

/// <summary>判定の根拠コード。文字列化は UI 層で行う（テスト可能性と多言語化のため）。</summary>
public enum ReasonCode
{
    NotNtfs,
    DirectStorageDetected,
    AntiCheatDetected,
    AlreadyCompressed,
    LowCompressibility,
    SavingTooSmall,
    RecentlyUpdated,
    UpdateStable,
    LargeSaving,
    NeverPlayed,
    LongUnplayed,
    RecentlyPlayed,
    /// <summary>削除のほうが圧縮より大幅に多く空く、という事実。</summary>
    DeleteYieldsMuchMore,
    ScanTruncated,
    NotFullyInstalled,
}

/// <summary>
/// 「アンインストールしたほうが得」という情報の出し方。
/// ツールが削除を実行することは無い。あくまで事実の提示のみ。
/// </summary>
public enum UninstallAdviceMode
{
    /// <summary>専用のバッジ・行として目立たせる。</summary>
    Prominent,

    /// <summary>補足の一行に留める。</summary>
    Subtle,

    /// <summary>一切表示しない。</summary>
    Off,
}

/// <summary>1 タイトルの総合評価。</summary>
public sealed record GameAssessment
{
    public required long AppId { get; init; }

    public required string Name { get; init; }

    public required string InstallPath { get; init; }

    public required AdviceKind Advice { get; init; }

    public required IReadOnlyList<ReasonCode> Reasons { get; init; }

    /// <summary>論理サイズ（バイト）。</summary>
    public required long SizeBytes { get; init; }

    /// <summary>ディスク上の実占有サイズ（バイト）。圧縮が効いていれば論理より小さい。</summary>
    public long PhysicalBytes { get; init; }

    /// <summary>圧縮見込み（比率・削減量）。</summary>
    public required CompressionEstimate Estimate { get; init; }

    /// <summary>最終プレイ。null = 一度も起動していない、または履歴不明。</summary>
    public DateTimeOffset? LastPlayed { get; init; }

    /// <summary>未起動日数。一度も起動していなければ null。</summary>
    public int? DaysSincePlayed { get; init; }

    /// <summary>最終更新からの経過日数。</summary>
    public int? DaysSinceUpdated { get; init; }

    public GameFeatures Features { get; init; }

    /// <summary>
    /// 削除した場合に空く量が、圧縮した場合の何倍か。
    /// 圧縮見込みがゼロに近いと無限大になるため上限を設けている。
    /// この数字を出すこと自体が「圧縮が最適解とは限らない」という誠実さの担保になる。
    /// </summary>
    public double DeleteVsCompressMultiple => Estimate.EstimatedSavedBytes > 0
        ? Math.Min((double)SizeBytes / Estimate.EstimatedSavedBytes, 999.0)
        : 999.0;

    /// <summary>削除を検討する価値があるか（事実としての判定。実行はしない）。</summary>
    public bool IsUninstallCandidate { get; init; }
}
