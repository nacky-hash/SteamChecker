using SteamChecker.Core.Analysis;
using SteamChecker.Core.Steam;

namespace SteamChecker.Tests;

/// <summary>時刻を固定するための TimeProvider。</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

public class AdvisorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 28, 0, 0, 0, TimeSpan.Zero);

    private const long GiB = 1024L * 1024 * 1024;

    private static Advisor CreateAdvisor(AdvisorOptions? options = null)
        => new(options, new FixedTimeProvider(Now));

    private static InstalledApp App(
        long sizeBytes = 60 * GiB,
        int daysSinceUpdate = 200,
        string name = "Test Game",
        long stateFlags = 4)
        => new()
        {
            AppId = 1,
            Name = name,
            InstallDir = name,
            FullPath = $@"D:\SteamLibrary\steamapps\common\{name}",
            SizeOnDisk = sizeBytes,
            LastUpdatedUnix = Now.AddDays(-daysSinceUpdate).ToUnixTimeSeconds(),
            StateFlags = stateFlags,
            Library = new SteamLibrary { Path = @"D:\SteamLibrary" },
        };

    private static FolderProfile Profile(
        long sizeBytes = 60 * GiB,
        GameFeatures features = GameFeatures.None)
        => new()
        {
            Path = @"D:\SteamLibrary\steamapps\common\Test Game",
            TotalLogicalBytes = sizeBytes,
            TotalPhysicalBytes = sizeBytes,
            FileCount = 100,
            BytesByCategory = new Dictionary<FileCategory, long> { [FileCategory.Executable] = sizeBytes },
            Features = features,
        };

    private static CompressionEstimate Estimate(double ratio, long sizeBytes = 60 * GiB)
        => new()
        {
            Ratio = ratio,
            EstimatedSavedBytes = (long)(sizeBytes * (1 - ratio)),
            Measured = true,
        };

    private static PlayRecord Played(int daysAgo)
        => new() { AppId = 1, LastPlayedUnix = Now.AddDays(-daysAgo).ToUnixTimeSeconds() };

    private static PlayRecord NeverPlayed()
        => new() { AppId = 1, LastPlayedUnix = 0 };

    // -----------------------------------------------------------------
    // 技術的な除外条件
    // -----------------------------------------------------------------

    [Fact]
    public void NTFS以外は圧縮非推奨()
    {
        var result = CreateAdvisor().Assess(
            App(), Profile(), Estimate(0.5), Played(10), isNtfs: false);

        Assert.Equal(AdviceKind.DoNotCompress, result.Advice);
        Assert.Contains(ReasonCode.NotNtfs, result.Reasons);
    }

    [Fact]
    public void DirectStorage対応は圧縮非推奨()
    {
        // GPU 展開の手前で CPU 展開が挟まり、DirectStorage の利点が相殺される。
        // 既存の圧縮ツールはこの判定を持たないので、ユーザーが自分で気づくしかない
        var result = CreateAdvisor().Assess(
            App(), Profile(features: GameFeatures.DirectStorage), Estimate(0.4), Played(5), true);

        Assert.Equal(AdviceKind.DoNotCompress, result.Advice);
        Assert.Contains(ReasonCode.DirectStorageDetected, result.Reasons);
    }

    [Fact]
    public void 既に圧縮済みなら何もしない()
    {
        var result = CreateAdvisor().Assess(
            App(), Profile(features: GameFeatures.AlreadyCompressed), Estimate(0.4), Played(5), true);

        Assert.Equal(AdviceKind.AlreadyCompressed, result.Advice);
    }

    [Fact]
    public void DirectStorageは既圧縮より優先して弾く()
    {
        var result = CreateAdvisor().Assess(
            App(),
            Profile(features: GameFeatures.DirectStorage | GameFeatures.AlreadyCompressed),
            Estimate(0.4), Played(5), true);

        Assert.Equal(AdviceKind.DoNotCompress, result.Advice);
    }

    // -----------------------------------------------------------------
    // 効果の大小
    // -----------------------------------------------------------------

    [Fact]
    public void 圧縮率が低ければ無駄と判定する()
    {
        var result = CreateAdvisor().Assess(
            App(), Profile(), Estimate(0.95), Played(10), true);

        Assert.Equal(AdviceKind.NotWorthCompressing, result.Advice);
        Assert.Contains(ReasonCode.LowCompressibility, result.Reasons);
    }

    [Fact]
    public void 削減量が小さすぎれば無駄と判定する()
    {
        // 率は良くても絶対量が小さければ意味がない（2GB の 50% = 1GB 未満）
        var small = 1 * GiB;
        var result = CreateAdvisor().Assess(
            App(small), Profile(small), Estimate(0.5, small), Played(10), true);

        Assert.Equal(AdviceKind.NotWorthCompressing, result.Advice);
        Assert.Contains(ReasonCode.SavingTooSmall, result.Reasons);
    }

    [Fact]
    public void 大きく削減できる場合はフラグが立つ()
    {
        var result = CreateAdvisor().Assess(
            App(), Profile(), Estimate(0.5), Played(10), true);

        Assert.Contains(ReasonCode.LargeSaving, result.Reasons);
    }

    // -----------------------------------------------------------------
    // 更新頻度と圧縮の持続性
    // -----------------------------------------------------------------

    [Fact]
    public void 更新が落ち着いていれば圧縮推奨()
    {
        var result = CreateAdvisor().Assess(
            App(daysSinceUpdate: 200), Profile(), Estimate(0.5), Played(3), true);

        Assert.Equal(AdviceKind.Compress, result.Advice);
        Assert.Contains(ReasonCode.UpdateStable, result.Reasons);
    }

    [Fact]
    public void 最近更新されたゲームは自動再圧縮を勧める()
    {
        // WOF 圧縮は書き込みで解除される。更新が頻繁なゲームは
        // 圧縮しっぱなしにできないので監視とセットでなければ意味がない
        var result = CreateAdvisor().Assess(
            App(daysSinceUpdate: 5), Profile(), Estimate(0.5), Played(3), true);

        Assert.Equal(AdviceKind.CompressWithWatcher, result.Advice);
        Assert.Contains(ReasonCode.RecentlyUpdated, result.Reasons);
    }

    [Fact]
    public void アンチチート検出時は必ず警告を挟む()
    {
        var result = CreateAdvisor().Assess(
            App(), Profile(features: GameFeatures.EasyAntiCheat), Estimate(0.5), Played(3), true);

        Assert.Equal(AdviceKind.CompressWithCaution, result.Advice);
        Assert.Contains(ReasonCode.AntiCheatDetected, result.Reasons);
    }

    // -----------------------------------------------------------------
    // プレイ履歴 — 企画の中核
    // -----------------------------------------------------------------

    [Fact]
    public void 一度も起動していないゲームは事実として記録される()
    {
        var result = CreateAdvisor().Assess(
            App(), Profile(), Estimate(0.5), NeverPlayed(), true);

        Assert.Contains(ReasonCode.NeverPlayed, result.Reasons);
        Assert.Null(result.LastPlayed);
    }

    [Fact]
    public void 長期未起動を判定する()
    {
        var result = CreateAdvisor().Assess(
            App(), Profile(), Estimate(0.5), Played(400), true);

        Assert.Contains(ReasonCode.LongUnplayed, result.Reasons);
        Assert.Equal(400, result.DaysSincePlayed);
    }

    [Fact]
    public void 最近遊んでいれば長期未起動にはならない()
    {
        var result = CreateAdvisor().Assess(
            App(), Profile(), Estimate(0.5), Played(10), true);

        Assert.Contains(ReasonCode.RecentlyPlayed, result.Reasons);
        Assert.DoesNotContain(ReasonCode.LongUnplayed, result.Reasons);
    }

    [Fact]
    public void プレイ履歴が無い場合は未起動として扱う()
    {
        var result = CreateAdvisor().Assess(App(), Profile(), Estimate(0.5), null, true);

        Assert.Contains(ReasonCode.NeverPlayed, result.Reasons);
    }

    // -----------------------------------------------------------------
    // 「削除のほうが得」の判定
    //
    // 企画の当初案は「未起動ゲームこそ圧縮に値する」だったが、
    // 未起動 × 大容量 × 圧縮が効かない、の組み合わせでは
    // 圧縮より削除のほうが桁違いに多く空く。
    // ツールは削除を実行しない。事実だけを出す。
    // -----------------------------------------------------------------

    [Fact]
    public void 未起動かつ大容量かつ圧縮が効かないなら削除候補として事実を出す()
    {
        var size = 60 * GiB;
        var result = CreateAdvisor().Assess(
            App(size), Profile(size), Estimate(0.92, size), Played(400), true);

        Assert.True(result.IsUninstallCandidate);
        Assert.Contains(ReasonCode.DeleteYieldsMuchMore, result.Reasons);

        // 60GB のうち圧縮で空くのは 4.8GB → 削除なら 12.5 倍
        Assert.InRange(result.DeleteVsCompressMultiple, 12.0, 13.0);
    }

    [Fact]
    public void 最近遊んでいるゲームは削除候補にしない()
    {
        var size = 60 * GiB;
        var result = CreateAdvisor().Assess(
            App(size), Profile(size), Estimate(0.92, size), Played(10), true);

        Assert.False(result.IsUninstallCandidate);
    }

    [Fact]
    public void 小さいゲームは削除候補にしない()
    {
        // 5GB を消しても体感は変わらない。提案するだけ鬱陶しい
        var size = 5 * GiB;
        var result = CreateAdvisor().Assess(
            App(size), Profile(size), Estimate(0.92, size), Played(400), true);

        Assert.False(result.IsUninstallCandidate);
    }

    [Fact]
    public void よく縮むゲームは未起動でも削除候補にしない()
    {
        // 圧縮で 30GB 空くなら、消さずに済ませたい人の選択肢を潰す必要はない
        var size = 60 * GiB;
        var result = CreateAdvisor().Assess(
            App(size), Profile(size), Estimate(0.5, size), Played(400), true);

        Assert.False(result.IsUninstallCandidate);
        Assert.Equal(AdviceKind.Compress, result.Advice);
    }

    [Fact]
    public void 削除提案をオフにできる()
    {
        var options = new AdvisorOptions { UninstallAdvice = UninstallAdviceMode.Off };
        var size = 60 * GiB;

        var result = CreateAdvisor(options).Assess(
            App(size), Profile(size), Estimate(0.92, size), Played(400), true);

        Assert.False(result.IsUninstallCandidate);
        Assert.DoesNotContain(ReasonCode.DeleteYieldsMuchMore, result.Reasons);
    }

    [Fact]
    public void 圧縮非推奨でも削除の事実は残す()
    {
        // DirectStorage で圧縮できない場合、唯一の代替案が削除になる。
        // ここで何も出さないとユーザーは詰む
        var size = 60 * GiB;
        var result = CreateAdvisor().Assess(
            App(size),
            Profile(size, GameFeatures.DirectStorage),
            Estimate(0.92, size), Played(400), true);

        Assert.Equal(AdviceKind.DoNotCompress, result.Advice);
        Assert.True(result.IsUninstallCandidate);
    }

    // -----------------------------------------------------------------
    // その他
    // -----------------------------------------------------------------

    [Fact]
    public void インストール未完了を報告する()
    {
        var result = CreateAdvisor().Assess(
            App(stateFlags: 1026), Profile(), Estimate(0.5), Played(10), true);

        Assert.Contains(ReasonCode.NotFullyInstalled, result.Reasons);
    }

    [Fact]
    public void インストール未完了は圧縮推奨まで素通りさせない()
    {
        // ダウンロード中・更新中に compact を当てると Steam の書き込みと競合する。
        // 理由コードだけ積んで AdviceKind.Compress を返すのは実バグだった（2026-07-30 レビューで発見）
        var result = CreateAdvisor().Assess(
            App(stateFlags: 1026), Profile(), Estimate(0.5), Played(10), true);

        Assert.Equal(AdviceKind.DoNotCompress, result.Advice);
    }

    [Fact]
    public void 走査が打ち切られたことを報告する()
    {
        var profile = Profile() with { Truncated = true };

        var result = CreateAdvisor().Assess(App(), profile, Estimate(0.5), Played(10), true);

        Assert.Contains(ReasonCode.ScanTruncated, result.Reasons);
    }

    [Fact]
    public void 閾値を設定で変えられる()
    {
        var strict = new AdvisorOptions { MinSavedFraction = 0.60 };

        var lenient = CreateAdvisor().Assess(App(), Profile(), Estimate(0.5), Played(10), true);
        var strictResult = CreateAdvisor(strict).Assess(App(), Profile(), Estimate(0.5), Played(10), true);

        Assert.Equal(AdviceKind.Compress, lenient.Advice);
        Assert.Equal(AdviceKind.NotWorthCompressing, strictResult.Advice);
    }

    [Fact]
    public void 削減ゼロでも倍率計算で例外にならない()
    {
        var result = CreateAdvisor().Assess(
            App(), Profile(), Estimate(1.0), Played(400), true);

        Assert.Equal(999.0, result.DeleteVsCompressMultiple);
    }

    [Fact]
    public void 論理サイズが取れない場合はSteam報告値を使う()
    {
        var profile = Profile() with { TotalLogicalBytes = 0 };

        var result = CreateAdvisor().Assess(App(50 * GiB), profile, Estimate(0.5), Played(10), true);

        Assert.Equal(50 * GiB, result.SizeBytes);
    }
}
