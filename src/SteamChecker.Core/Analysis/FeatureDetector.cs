namespace SteamChecker.Core.Analysis;

/// <summary>
/// ゲームフォルダから検出した「圧縮判断に影響する特徴」。
/// 既存の圧縮ツール（CompactGUI / Game Compressor / SNUG）はいずれもこの判定を持たない。
/// </summary>
[Flags]
public enum GameFeatures
{
    None = 0,

    /// <summary>
    /// DirectStorage 対応。GPU 展開の手前で CPU 展開が挟まり、
    /// DirectStorage の利点が相殺されるため圧縮すべきでない。
    /// </summary>
    DirectStorage = 1 << 0,

    /// <summary>Easy Anti-Cheat を使用。</summary>
    EasyAntiCheat = 1 << 1,

    /// <summary>BattlEye を使用。</summary>
    BattlEye = 1 << 2,

    /// <summary>その他のアンチチート / DRM。</summary>
    OtherAntiCheat = 1 << 3,

    /// <summary>既に WOF 圧縮が適用されている形跡がある。</summary>
    AlreadyCompressed = 1 << 4,
}

public static class GameFeaturesExtensions
{
    public static bool HasAnyAntiCheat(this GameFeatures f)
        => (f & (GameFeatures.EasyAntiCheat | GameFeatures.BattlEye | GameFeatures.OtherAntiCheat)) != 0;
}

/// <summary>
/// フォルダ走査中にファイル名を流し込んで特徴を検出する累積器。
/// 走査は 1 パスで済ませたいので、プロファイラと同じループから呼ぶ設計にしている。
/// </summary>
public sealed class FeatureDetector
{
    private GameFeatures _features = GameFeatures.None;

    private static readonly string[] DirectStorageFiles =
    [
        "dstorage.dll",
        "dstoragecore.dll",
    ];

    private static readonly string[] EasyAntiCheatFiles =
    [
        "easyanticheat.exe",
        "easyanticheat_x64.dll",
        "easyanticheat_x86.dll",
        "easyanticheat_eos_setup.exe",
        "start_protected_game.exe",
    ];

    private static readonly string[] BattlEyeFiles =
    [
        "beservice.exe",
        "beservice_x64.exe",
        "beclient.dll",
        "beclient_x64.dll",
        "belauncher.exe",
    ];

    private static readonly string[] OtherAntiCheatFiles =
    [
        "gameguard.des",       // nProtect GameGuard
        "gamemon.des",
        "xigncode3.xem",       // XIGNCODE3
        "anticheatexpert.exe", // ACE
        "aceclient.dll",
        "rzr_anticheat.dll",
        "vgc.exe",             // Riot Vanguard (通常はシステム側だが同梱例あり)
    ];

    /// <summary>1 ファイル分のファイル名（拡張子込み、ディレクトリ不要）を投入する。</summary>
    public void Feed(string fileName)
    {
        var lower = fileName.ToLowerInvariant();

        if (Contains(DirectStorageFiles, lower)) _features |= GameFeatures.DirectStorage;
        if (Contains(EasyAntiCheatFiles, lower)) _features |= GameFeatures.EasyAntiCheat;
        if (Contains(BattlEyeFiles, lower)) _features |= GameFeatures.BattlEye;
        if (Contains(OtherAntiCheatFiles, lower)) _features |= GameFeatures.OtherAntiCheat;
    }

    private static bool Contains(string[] set, string value)
    {
        foreach (var s in set)
        {
            if (string.Equals(s, value, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    public void MarkAlreadyCompressed() => _features |= GameFeatures.AlreadyCompressed;

    public GameFeatures Result => _features;
}
