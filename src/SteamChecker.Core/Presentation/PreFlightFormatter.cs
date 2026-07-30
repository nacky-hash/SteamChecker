using SteamChecker.Core.Compression;

namespace SteamChecker.Core.Presentation;

/// <summary>事前検査結果の日本語文言。文言はロジックから分離する（AdviceFormatter と同方針）。</summary>
public static class PreFlightFormatter
{
    public static string Describe(PreFlightIssue issue) => issue.CheckId switch
    {
        PreFlightCheckId.TargetInsideLibrary =>
            $"対象が Steam ライブラリの steamapps\\common 配下ではありません: {issue.Detail}",

        PreFlightCheckId.ManifestStillExists =>
            "appmanifest が見つかりません。走査後にアンインストール・移動された可能性があります",

        PreFlightCheckId.TargetExists =>
            $"対象フォルダが見つかりません: {issue.Detail}",

        PreFlightCheckId.NtfsFileSystem =>
            $"NTFS ではないため透過圧縮を適用できません（検出: {issue.Detail}）",

        PreFlightCheckId.NoReparsePoint =>
            $"ジャンクション / シンボリックリンクを検出しました。リンク先を巻き込まないよう中止します: {issue.Detail}",

        PreFlightCheckId.SteamNotRunning =>
            "Steam クライアントが起動中です。終了してから実行してください（更新・ダウンロードとの競合を避けるため）",

        PreFlightCheckId.GameNotRunning =>
            $"ゲームの実行ファイルが使用中です: {issue.Detail}",

        PreFlightCheckId.DirectStorageSigns =>
            "DirectStorage の兆候（dstorage*.dll）を検出しました。圧縮すると読み込みが遅くなる可能性があります",

        PreFlightCheckId.SizeMeasurable =>
            "対象フォルダの容量を測定できませんでした",

        PreFlightCheckId.EnoughFreeSpace =>
            $"作業に必要な空き容量が不足しています（{issue.Detail}）",

        _ => issue.CheckId.ToString(),
    };
}
