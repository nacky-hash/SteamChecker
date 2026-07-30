using System.Globalization;
using SteamChecker.Core.Analysis;

namespace SteamChecker.Core.Presentation;

/// <summary>
/// 判定結果を日本語の文字列にする。
///
/// 文言はここに集約する。判定ロジック側は ReasonCode を返すだけにしてあるので、
/// 文言を変えてもテストは壊れないし、多言語化するときもここだけを差し替えればよい。
///
/// 表現方針: 命令しない。事実だけを書く。
///   ×「削除してください」
///   ○「3年2ヶ月 未起動 / 60.2GB / 圧縮見込み 4.8GB」
/// 判断はユーザーがする。
/// </summary>
public static class AdviceFormatter
{
    public static string Label(AdviceKind kind) => kind switch
    {
        AdviceKind.Compress => "圧縮推奨",
        AdviceKind.CompressWithWatcher => "圧縮可（要 自動再圧縮）",
        AdviceKind.CompressWithCaution => "圧縮可（要 確認）",
        AdviceKind.NotWorthCompressing => "圧縮しても効果小",
        AdviceKind.DoNotCompress => "圧縮非推奨",
        AdviceKind.AlreadyCompressed => "圧縮済み",
        _ => "不明",
    };

    public static string ShortLabel(AdviceKind kind) => kind switch
    {
        AdviceKind.Compress => "圧縮",
        AdviceKind.CompressWithWatcher => "圧縮+監視",
        AdviceKind.CompressWithCaution => "圧縮(確認)",
        AdviceKind.NotWorthCompressing => "効果小",
        AdviceKind.DoNotCompress => "非推奨",
        AdviceKind.AlreadyCompressed => "済",
        _ => "-",
    };

    public static string Describe(ReasonCode code, GameAssessment context) => code switch
    {
        ReasonCode.NotNtfs =>
            "NTFS 以外のドライブのため、Windows の透過圧縮は使えません",

        ReasonCode.DirectStorageDetected =>
            "DirectStorage 対応。圧縮すると GPU 展開の前に CPU 展開が挟まり、読み込みが遅くなる可能性があります",

        ReasonCode.AntiCheatDetected =>
            "アンチチートを検出。透過圧縮はファイル内容を変えないため理論上は問題ありませんが、実績が少ないため既定では対象外にしています",

        ReasonCode.AlreadyCompressed =>
            "すでに圧縮されています",

        // 「実測したところ」と言えるのは Estimate.Measured のときだけ（D-011 の再発防止）
        ReasonCode.LowCompressibility =>
            context.Estimate.Measured
                ? $"実測したところ {context.Estimate.SavedFraction:P0} しか縮みません（動画・音声・圧縮済みテクスチャが中心）"
                : $"ファイル構成からの推定では {context.Estimate.SavedFraction:P0} しか縮みません（実測サンプルなし）",

        ReasonCode.SavingTooSmall =>
            $"縮む量が {Bytes(context.Estimate.EstimatedSavedBytes)} にとどまります",

        ReasonCode.RecentlyUpdated =>
            $"{context.DaysSinceUpdated} 日前に更新。圧縮は書き込みで解除されるため、更新のたびに再圧縮が必要です",

        ReasonCode.UpdateStable =>
            context.DaysSinceUpdated is { } d
                ? $"最終更新から {d} 日。圧縮した状態が保たれやすい構成です"
                : "更新の記録がありません",

        ReasonCode.LargeSaving =>
            $"{Bytes(context.Estimate.EstimatedSavedBytes)} 空きます",

        ReasonCode.NeverPlayed =>
            "起動記録がありません",

        ReasonCode.LongUnplayed =>
            $"{Duration(context.DaysSincePlayed)} 未起動",

        ReasonCode.RecentlyPlayed =>
            context.DaysSincePlayed is { } d ? $"{Duration(d)} 前にプレイ" : "最近プレイ",

        ReasonCode.DeleteYieldsMuchMore =>
            $"削除すれば {Bytes(context.SizeBytes)} 空きます（圧縮の約 {context.DeleteVsCompressMultiple:F0} 倍）",

        ReasonCode.ScanTruncated =>
            "ファイル数が多いため途中まで走査しました。見積もりの精度が落ちている可能性があります",

        ReasonCode.NotFullyInstalled =>
            "インストールが完了していません（ダウンロード中・更新中の可能性）",

        _ => code.ToString(),
    };

    /// <summary>バイト数を人が読める形式にする。基数は 1024。</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 0) return "-" + Bytes(-bytes);
        if (bytes < 1024) return $"{bytes} B";

        string[] units = ["KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var unit = -1;

        do
        {
            value /= 1024;
            unit++;
        } while (value >= 1024 && unit < units.Length - 1);

        var format = value >= 100 ? "F0" : value >= 10 ? "F1" : "F2";
        return value.ToString(format, CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>日数を「3年2ヶ月」「5ヶ月」「12日」のような表現にする。</summary>
    public static string Duration(int? days)
    {
        if (days is null) return "不明";
        if (days < 0) return "0日";
        if (days < 31) return $"{days}日";

        var years = days.Value / 365;
        var months = days.Value % 365 / 30;

        if (years > 0) return months > 0 ? $"{years}年{months}ヶ月" : $"{years}年";
        return $"{months}ヶ月";
    }

    /// <summary>1 行サマリ。命令形を使わず、事実だけを並べる。</summary>
    public static string OneLineSummary(GameAssessment a)
    {
        var played = a.DaysSincePlayed switch
        {
            null => "起動記録なし",
            // 「1日未起動」は日本語として不自然かつ印象操作になる。
            // 最近遊んでいるものは「いつ遊んだか」として書く
            < 31 and var d => $"{Duration(d)}前にプレイ",
            var d => $"{Duration(d)}未起動",
        };

        // 既に圧縮済みのものに「圧縮見込み N GB」と出すと、
        // 実現済みの削減量をこれから空く量として二重計上しているように読める。
        // 実際に効いている削減量（論理 − 実占有）を示す
        var saving = a.Advice == AdviceKind.AlreadyCompressed
            ? $"圧縮済み（{Bytes(Math.Max(0, a.SizeBytes - a.PhysicalBytes))} 削減中）"
            : a.Estimate.EstimatedSavedBytes > 0
                ? $"圧縮見込み {Bytes(a.Estimate.EstimatedSavedBytes)}"
                : "圧縮見込み ほぼなし";

        return $"{played} / {Bytes(a.SizeBytes)} / {saving}";
    }
}
