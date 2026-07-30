using SteamChecker.Core.Analysis;
using SteamChecker.Core.Presentation;

namespace SteamChecker.Tests;

public class AdviceFormatterTests
{
    private const long GiB = 1024L * 1024 * 1024;

    private static GameAssessment Assessment(bool measured) => new()
    {
        AppId = 1,
        Name = "Test Game",
        InstallPath = @"D:\SteamLibrary\steamapps\common\Test Game",
        Advice = AdviceKind.NotWorthCompressing,
        Reasons = [ReasonCode.LowCompressibility],
        SizeBytes = 60 * GiB,
        Estimate = new CompressionEstimate
        {
            Ratio = 0.98,
            EstimatedSavedBytes = (long)(60 * GiB * 0.02),
            Measured = measured,
        },
        LastPlayed = null,
        DaysSincePlayed = null,
        DaysSinceUpdated = null,
        Features = GameFeatures.None,
        IsUninstallCandidate = false,
    };

    [Fact]
    public void 実測していないのに実測したと表示しない()
    {
        // D-011 の事故の再発防止: Measured=false（全カテゴリで実測を省略した場合など）で
        // 「実測したところ」と表示するのは、実測していないものを実測と偽ること
        var text = AdviceFormatter.Describe(ReasonCode.LowCompressibility, Assessment(measured: false));

        Assert.DoesNotContain("実測したところ", text);
    }

    [Fact]
    public void 実測済みなら実測と表示してよい()
    {
        var text = AdviceFormatter.Describe(ReasonCode.LowCompressibility, Assessment(measured: true));

        Assert.Contains("実測", text);
    }

    [Fact]
    public void 圧縮済みタイトルに圧縮見込みを表示しない()
    {
        // 既に実現した削減量を「これから空く量」として出すと二重計上に読める。
        // 実際に効いている削減量（論理 − 実占有）を示すこと
        var a = Assessment(measured: true) with
        {
            Advice = AdviceKind.AlreadyCompressed,
            SizeBytes = 100 * GiB,
            PhysicalBytes = 60 * GiB,
        };

        var text = AdviceFormatter.OneLineSummary(a);

        Assert.DoesNotContain("圧縮見込み", text);
        Assert.Contains("圧縮済み", text);
        Assert.Contains("40.0 GB", text);
    }

    [Fact]
    public void 圧縮していないタイトルには圧縮見込みを表示する()
    {
        var a = Assessment(measured: true) with { Advice = AdviceKind.Compress };

        Assert.Contains("圧縮見込み", AdviceFormatter.OneLineSummary(a));
    }
}
