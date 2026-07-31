using System.Text.RegularExpressions;
using SteamChecker.Core.Analysis;
using SteamChecker.Core.Presentation;

namespace SteamChecker.Tests;

/// <summary>
/// 配色の網羅を保証する。
///
/// 色は「付いていなくても動く」ため、抜けても誰も気づかない。
/// 分類を増やしたのに色を足し忘れると、その分類だけ黙って灰色になる。
/// これは 2026-07-31 に踏んだ「グループが描画されて初めて壊れる」のと同じ構造なので、
/// 目視ではなくテストで押さえる。
/// </summary>
public class AdviceColorsTests
{
    public static IEnumerable<object[]> AllKinds()
        => Enum.GetValues<AdviceKind>().Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void 全ての分類に専用の色が割り当てられている(AdviceKind kind)
    {
        var background = AdviceColors.Background(kind);
        var accent = AdviceColors.Accent(kind);

        Assert.NotEqual(AdviceColors.NeutralBackground, background);
        Assert.NotEqual(AdviceColors.NeutralAccent, accent);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void 色は有効な16進表記である(AdviceKind kind)
    {
        Assert.Matches("^#[0-9A-Fa-f]{6}$", AdviceColors.Background(kind));
        Assert.Matches("^#[0-9A-Fa-f]{6}$", AdviceColors.Accent(kind));
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void 見出しの文字列から色を引ける(AdviceKind kind)
    {
        // UI はグループのキー（ラベル文字列）しか持っていない。
        // ラベルを変えたときに対応が切れると、その分類だけ色が消える
        var label = AdviceFormatter.Label(kind);

        var (background, accent) = AdviceColors.ByLabel(label);

        Assert.Equal(AdviceColors.Background(kind), background);
        Assert.Equal(AdviceColors.Accent(kind), accent);
    }

    [Fact]
    public void 分類ごとに色が重複していない()
    {
        // 同じ色だと分類の区別という目的を果たさない
        var accents = Enum.GetValues<AdviceKind>().Select(AdviceColors.Accent).ToList();

        Assert.Equal(accents.Count, accents.Distinct().Count());
    }

    [Fact]
    public void 未知の見出しは中立色に倒す()
    {
        // 解析前の「未解析（サイズの大きい順）」など。例外にせず色が付かないだけにする
        var (background, accent) = AdviceColors.ByLabel("未解析（サイズの大きい順）");

        Assert.Equal(AdviceColors.NeutralBackground, background);
        Assert.Equal(AdviceColors.NeutralAccent, accent);
    }

    [Fact]
    public void 見出しの文言に存在しない機能を書かない()
    {
        // 「自動再圧縮」は常駐監視を指しており、その機能は作らない方針（D-018）。
        // 実装していない機能を前提にしたラベルは虚偽表示になる
        foreach (var kind in Enum.GetValues<AdviceKind>())
        {
            var label = AdviceFormatter.Label(kind);

            Assert.DoesNotContain("自動再圧縮", label);
            Assert.DoesNotContain("監視", label);
        }
    }
}
