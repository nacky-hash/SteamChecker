using SteamChecker.Core.Analysis;

namespace SteamChecker.Core.Presentation;

/// <summary>
/// 分類ごとの配色。文言（<see cref="AdviceFormatter"/>）と同じ Presentation 層に置く。
///
/// UI 層に置かなかった理由: 配色表を WPF 側に置くと、
/// 「分類を増やしたのに色を足し忘れて、黙って灰色になる」事故がテストで検出できない。
/// ここに置けば OS 非依存のテストで全分類の網羅を保証できる（D-009 を壊さない）。
///
/// 【配色の方針】
///   - 色だけに意味を持たせない。意味は文字（Label）が担い、色は探しやすさの補助
///   - やっていい=緑 / 条件つき=青・橙 / やらなくていい=灰 / やるな=赤
///   - 緑と赤は明度差もつける（色相差が出ない色覚の型があるため）
/// </summary>
public static class AdviceColors
{
    /// <summary>見出しの背景色（薄い色）。</summary>
    public static string Background(AdviceKind kind) => kind switch
    {
        AdviceKind.Compress => "#E7F6EC",
        AdviceKind.CompressUpdatesOften => "#E8F1FB",
        AdviceKind.CompressAntiCheat => "#FDF3E3",
        AdviceKind.NotWorthCompressing => "#EBEBEB",
        AdviceKind.DoNotCompress => "#FBEAEA",
        AdviceKind.AlreadyCompressed => "#EDF0F4",
        _ => NeutralBackground,
    };

    /// <summary>見出しの文字とアクセントバーの色（濃い色）。</summary>
    public static string Accent(AdviceKind kind) => kind switch
    {
        AdviceKind.Compress => "#2E9E52",
        AdviceKind.CompressUpdatesOften => "#2E6FB8",
        AdviceKind.CompressAntiCheat => "#C7841A",
        AdviceKind.NotWorthCompressing => "#767676",
        AdviceKind.DoNotCompress => "#B33A3A",
        AdviceKind.AlreadyCompressed => "#5B6B80",
        _ => NeutralAccent,
    };

    /// <summary>
    /// 分類に属さない見出し（解析前の一覧など）の色。
    ///
    /// **どの分類の色とも一致させないこと。**
    /// 一致していると「色の割り当てが壊れて中立色に落ちた」状態と
    /// 「その分類である」状態が画面上で区別できず、故障が故障に見えなくなる。
    /// 実際 2026-07-31 に「圧縮しても効果小」と同色になっていて、テストで検出した。
    /// </summary>
    public const string NeutralBackground = "#F7F7F7";

    public const string NeutralAccent = "#A0A0A0";

    /// <summary>
    /// 見出しの文字列から色を引く。UI はグループのキー（＝ラベル文字列）しか持たないため。
    /// 未知のラベルは中立色に倒す（例外にしない。色が付かないだけで機能は死なない）。
    /// </summary>
    public static (string Background, string Accent) ByLabel(string? label)
    {
        if (label is null) return (NeutralBackground, NeutralAccent);

        foreach (var kind in Enum.GetValues<AdviceKind>())
        {
            if (AdviceFormatter.Label(kind) == label) return (Background(kind), Accent(kind));
        }

        return (NeutralBackground, NeutralAccent);
    }
}
