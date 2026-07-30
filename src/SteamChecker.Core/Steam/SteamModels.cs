namespace SteamChecker.Core.Steam;

/// <summary>Steam ライブラリフォルダ 1 つ分。</summary>
public sealed record SteamLibrary
{
    /// <summary>ライブラリのルート（steamapps の親）。</summary>
    public required string Path { get; init; }

    /// <summary>ラベル（libraryfolders.vdf の label。通常は空）。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>libraryfolders.vdf が報告する総容量（バイト）。0 なら不明。</summary>
    public long TotalSize { get; init; }

    /// <summary>
    /// steamapps フォルダのパス。
    /// System.IO.Path.Combine は使わない — Linux 上でテストすると '/' で連結され、
    /// Windows のパスを扱うテストが実環境と違う経路を通ってしまうため、
    /// 元のパスに含まれる区切り文字をそのまま踏襲する。
    /// </summary>
    public string SteamAppsPath
    {
        get
        {
            var separator = Path.Contains('\\') ? '\\'
                : Path.Contains('/') ? '/'
                : System.IO.Path.DirectorySeparatorChar;

            return Path.TrimEnd('\\', '/') + separator + "steamapps";
        }
    }
}

/// <summary>appmanifest_&lt;appid&gt;.acf から読み取ったインストール情報。</summary>
public sealed record InstalledApp
{
    public required long AppId { get; init; }

    public required string Name { get; init; }

    /// <summary>steamapps/common 配下のフォルダ名。</summary>
    public required string InstallDir { get; init; }

    /// <summary>ゲームフォルダの絶対パス。</summary>
    public required string FullPath { get; init; }

    /// <summary>Steam が報告するインストールサイズ（バイト）。信用しすぎない — 実測と併用する。</summary>
    public long SizeOnDisk { get; init; }

    /// <summary>最終更新（Unix 秒）。0 なら不明。</summary>
    public long LastUpdatedUnix { get; init; }

    /// <summary>StateFlags。4 = 完全インストール済み。</summary>
    public long StateFlags { get; init; }

    /// <summary>属するライブラリ。</summary>
    public required SteamLibrary Library { get; init; }

    public DateTimeOffset? LastUpdated => LastUpdatedUnix > 0
        ? DateTimeOffset.FromUnixTimeSeconds(LastUpdatedUnix)
        : null;

    /// <summary>StateFlags のビット 2 (=4) が立っていれば完全インストール済み。</summary>
    public bool IsFullyInstalled => (StateFlags & 4) != 0;
}

/// <summary>localconfig.vdf から読み取ったプレイ履歴。</summary>
public sealed record PlayRecord
{
    public required long AppId { get; init; }

    /// <summary>最終プレイ（Unix 秒）。0 = 一度も起動していない。</summary>
    public long LastPlayedUnix { get; init; }

    /// <summary>累計プレイ時間（分）。localconfig に無い場合は 0。</summary>
    public long PlaytimeMinutes { get; init; }

    public DateTimeOffset? LastPlayed => LastPlayedUnix > 0
        ? DateTimeOffset.FromUnixTimeSeconds(LastPlayedUnix)
        : null;

    public bool NeverPlayed => LastPlayedUnix <= 0;
}

/// <summary>Steam のローカルユーザー（userdata 配下の 1 アカウント）。</summary>
public sealed record SteamUser
{
    /// <summary>userdata 配下のフォルダ名（= AccountID / SteamID3）。</summary>
    public required long AccountId { get; init; }

    public required string LocalConfigPath { get; init; }

    /// <summary>表示名。取得できなければ空。</summary>
    public string PersonaName { get; init; } = string.Empty;

    /// <summary>localconfig.vdf の最終更新時刻。複数アカウント時に「直近使ったユーザー」を推定するのに使う。</summary>
    public DateTimeOffset ConfigModifiedUtc { get; init; }
}
