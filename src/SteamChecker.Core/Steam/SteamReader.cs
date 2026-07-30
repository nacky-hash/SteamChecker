using System.Globalization;

namespace SteamChecker.Core.Steam;

/// <summary>
/// Steam のローカル設定ファイル群を読む。ネットワークアクセスは一切行わない。
/// Steam Web API を使わない設計判断の理由:
///   - API キー不要 / プロフィール公開不要 / オフライン動作可能
///   - 個人情報を外部に送信しないと明言できる（無料配布ツールの信頼性として重要）
/// </summary>
public sealed class SteamReader(IFileSystem fs)
{
    private readonly IFileSystem _fs = fs;

    public SteamReader() : this(new PhysicalFileSystem()) { }

    // ---------------------------------------------------------------
    // libraryfolders.vdf
    // ---------------------------------------------------------------

    /// <summary>
    /// &lt;Steam&gt;/steamapps/libraryfolders.vdf を読んで全ライブラリフォルダを返す。
    /// 実在しないパス（取り外した外付けドライブ等）は除外する。
    /// </summary>
    public IReadOnlyList<SteamLibrary> ReadLibraries(string steamRoot)
    {
        var vdfPath = _fs.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!_fs.FileExists(vdfPath))
        {
            // 旧形式のフォールバック: steamapps だけは必ず存在する
            var fallback = new SteamLibrary { Path = steamRoot };
            return _fs.DirectoryExists(fallback.SteamAppsPath) ? [fallback] : [];
        }

        var root = VdfParser.Parse(_fs.ReadAllText(vdfPath));

        // 形式は 2 通りある:
        //   旧: "LibraryFolders" { "1" "D:\\SteamLibrary" ... }
        //   新: "libraryfolders" { "0" { "path" "C:\\..." "totalsize" "..." } ... }
        var container = root.Find("libraryfolders") ?? root.Find("LibraryFolders") ?? root;

        var result = new List<SteamLibrary>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, node) in container.NumericChildren())
        {
            SteamLibrary? lib = null;

            if (node.IsValue)
            {
                lib = new SteamLibrary { Path = node.Value! };
            }
            else if (node.GetString("path") is { Length: > 0 } path)
            {
                lib = new SteamLibrary
                {
                    Path = path,
                    Label = node.GetString("label") ?? string.Empty,
                    TotalSize = node.GetInt64OrDefault(0, "totalsize"),
                };
            }

            if (lib is null) continue;

            var normalized = NormalizePath(lib.Path);
            if (!seen.Add(normalized)) continue;
            if (!_fs.DirectoryExists(lib.SteamAppsPath)) continue;

            result.Add(lib);
        }

        // libraryfolders.vdf に自分自身が載っていない古い環境への保険
        var self = new SteamLibrary { Path = steamRoot };
        if (seen.Add(NormalizePath(steamRoot)) && _fs.DirectoryExists(self.SteamAppsPath))
        {
            result.Insert(0, self);
        }

        return result;
    }

    private static string NormalizePath(string path)
        => path.Replace('\\', '/').TrimEnd('/');

    // ---------------------------------------------------------------
    // appmanifest_<appid>.acf
    // ---------------------------------------------------------------

    /// <summary>ライブラリ配下の appmanifest_*.acf を全て読む。壊れたファイルは黙って飛ばす。</summary>
    public IReadOnlyList<InstalledApp> ReadInstalledApps(SteamLibrary library)
    {
        var steamApps = library.SteamAppsPath;
        if (!_fs.DirectoryExists(steamApps)) return [];

        var result = new List<InstalledApp>();

        foreach (var file in _fs.EnumerateFiles(steamApps, "appmanifest_*.acf"))
        {
            var app = TryReadAppManifest(file, library);
            if (app is not null) result.Add(app);
        }

        return result;
    }

    public InstalledApp? TryReadAppManifest(string acfPath, SteamLibrary library)
    {
        try
        {
            var root = VdfParser.Parse(_fs.ReadAllText(acfPath));
            var state = root.Find("AppState");
            if (state is null) return null;

            if (!state.TryGetInt64(out var appId, "appid")) return null;

            var installDir = state.GetString("installdir");
            if (string.IsNullOrWhiteSpace(installDir)) return null;

            // installdir は steamapps\common 直下のフォルダ名でなければならない。
            // 区切り文字・".."・ドライブ指定を含む値は、そのまま Combine すると
            // ライブラリ外の任意パスを指せてしまう（パストラバーサル）。
            // 破損・改ざんされた manifest として扱い、読み込まない
            if (!Compression.PreFlightChecker.IsSafeInstallDir(installDir)) return null;

            var name = state.GetString("name");
            if (string.IsNullOrWhiteSpace(name)) name = installDir;

            return new InstalledApp
            {
                AppId = appId,
                Name = name,
                InstallDir = installDir,
                FullPath = _fs.Combine(library.SteamAppsPath, "common", installDir),
                SizeOnDisk = state.GetInt64OrDefault(0, "SizeOnDisk"),
                LastUpdatedUnix = state.GetInt64OrDefault(0, "LastUpdated"),
                StateFlags = state.GetInt64OrDefault(0, "StateFlags"),
                Library = library,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VdfParseException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------
    // userdata/<accountid>/config/localconfig.vdf
    // ---------------------------------------------------------------

    /// <summary>userdata 配下のローカルユーザーを列挙する。</summary>
    public IReadOnlyList<SteamUser> ReadUsers(string steamRoot)
    {
        var userdata = _fs.Combine(steamRoot, "userdata");
        if (!_fs.DirectoryExists(userdata)) return [];

        var users = new List<SteamUser>();

        foreach (var dir in _fs.EnumerateDirectories(userdata))
        {
            var name = _fs.GetFileName(dir);
            if (!long.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var accountId))
            {
                continue;
            }

            // accountId 0 は「ログインしていない」ダミー
            if (accountId <= 0) continue;

            var localConfig = _fs.Combine(dir, "config", "localconfig.vdf");
            if (!_fs.FileExists(localConfig)) continue;

            users.Add(new SteamUser
            {
                AccountId = accountId,
                LocalConfigPath = localConfig,
                PersonaName = TryReadPersonaName(localConfig),
                ConfigModifiedUtc = _fs.GetLastWriteTimeUtc(localConfig),
            });
        }

        // 直近に使われたアカウントを先頭に
        return users.OrderByDescending(u => u.ConfigModifiedUtc).ToList();
    }

    private string TryReadPersonaName(string localConfigPath)
    {
        try
        {
            var root = VdfParser.Parse(_fs.ReadAllText(localConfigPath));
            return root.GetString("UserLocalConfigStore", "friends", "PersonaName")
                   ?? root.GetString("UserLocalConfigStore", "friends", "personaname")
                   ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// localconfig.vdf からプレイ履歴を読む。
    /// 構造: UserLocalConfigStore &gt; Software &gt; Valve &gt; Steam &gt; apps &gt; &lt;appid&gt; &gt; LastPlayed
    /// </summary>
    public IReadOnlyDictionary<long, PlayRecord> ReadPlayRecords(SteamUser user)
    {
        var result = new Dictionary<long, PlayRecord>();

        VdfNode root;
        try
        {
            root = VdfParser.Parse(_fs.ReadAllText(user.LocalConfigPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VdfParseException)
        {
            return result;
        }

        // ルートキーは "UserLocalConfigStore"。念のため、無ければ直下も探す
        var apps = root.Find("UserLocalConfigStore", "Software", "Valve", "Steam", "apps")
                   ?? root.Find("Software", "Valve", "Steam", "apps");

        if (apps is null) return result;

        foreach (var (appId, node) in apps.NumericChildren())
        {
            if (node.IsValue) continue;

            result[appId] = new PlayRecord
            {
                AppId = appId,
                LastPlayedUnix = node.GetInt64OrDefault(0, "LastPlayed"),
                PlaytimeMinutes = node.GetInt64OrDefault(0, "Playtime"),
            };
        }

        return result;
    }

    /// <summary>
    /// 全ユーザーのプレイ履歴をマージする。同一 AppId は「より新しい LastPlayed」を採用。
    /// 家族共有・複数アカウント環境で「実際には遊ばれているのに未プレイ判定」になる事故を防ぐ。
    /// </summary>
    public IReadOnlyDictionary<long, PlayRecord> ReadMergedPlayRecords(IEnumerable<SteamUser> users)
    {
        var merged = new Dictionary<long, PlayRecord>();

        foreach (var user in users)
        {
            foreach (var (appId, record) in ReadPlayRecords(user))
            {
                if (merged.TryGetValue(appId, out var existing))
                {
                    merged[appId] = new PlayRecord
                    {
                        AppId = appId,
                        LastPlayedUnix = Math.Max(existing.LastPlayedUnix, record.LastPlayedUnix),
                        PlaytimeMinutes = Math.Max(existing.PlaytimeMinutes, record.PlaytimeMinutes),
                    };
                }
                else
                {
                    merged[appId] = record;
                }
            }
        }

        return merged;
    }
}
