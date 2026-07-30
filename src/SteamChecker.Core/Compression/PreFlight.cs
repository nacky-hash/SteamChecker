using SteamChecker.Core.Analysis;
using SteamChecker.Core.Steam;

namespace SteamChecker.Core.Compression;

/// <summary>事前検査の項目。</summary>
public enum PreFlightCheckId
{
    /// <summary>対象が検出済み Steam ライブラリの steamapps\common 配下か。</summary>
    TargetInsideLibrary,

    /// <summary>appmanifest が現在も存在するか（走査後に消えていないか / TOCTOU）。</summary>
    ManifestStillExists,

    /// <summary>対象フォルダが現在も存在するか。</summary>
    TargetExists,

    /// <summary>ファイルシステムが NTFS か。</summary>
    NtfsFileSystem,

    /// <summary>対象自身・親・子階層に reparse point（ジャンクション等）が無いか。</summary>
    NoReparsePoint,

    /// <summary>Steam クライアントが起動中でないか。</summary>
    SteamNotRunning,

    /// <summary>ゲーム内の実行ファイルが使用中（起動中）でないか。</summary>
    GameNotRunning,

    /// <summary>DirectStorage の兆候。兆候が無いことは安全の証明にはならない。</summary>
    DirectStorageSigns,

    /// <summary>容量が測定できるか。</summary>
    SizeMeasurable,

    /// <summary>圧縮・解除の作業に十分な空き容量があるか。</summary>
    EnoughFreeSpace,
}

/// <summary>検査結果 1 件。Block は実行を止める。Warn は表示して確認を求める。</summary>
public sealed record PreFlightIssue
{
    public required PreFlightCheckId CheckId { get; init; }

    public required bool Blocks { get; init; }

    /// <summary>補足情報（パスや数値）。文言化は Presentation 層で行う。</summary>
    public string? Detail { get; init; }
}

public sealed record PreFlightReport
{
    public required IReadOnlyList<PreFlightIssue> Issues { get; init; }

    /// <summary>Block が 1 件でもあれば実行してはならない。</summary>
    public bool CanProceed => Issues.All(i => !i.Blocks);

    public bool HasWarnings => Issues.Any(i => !i.Blocks);
}

public sealed record PreFlightOptions
{
    /// <summary>
    /// 空き容量の最低要求に上乗せする余裕。compact.exe はファイル単位で
    /// 圧縮コピーを作ってから差し替えるため、理論上の最低要求は
    /// 「最大ファイルの論理サイズ」。断定できないので余裕を持たせる。
    /// </summary>
    public long FreeSpaceMarginBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Steam クライアントとみなすプロセス名（小文字・拡張子なし）。</summary>
    public IReadOnlyList<string> SteamProcessNames { get; init; } = ["steam"];

    /// <summary>reparse point の子階層走査で見るディレクトリ数の上限。</summary>
    public int MaxDirectoriesToInspect { get; init; } = 10_000;
}

/// <summary>
/// 書き込みを伴う操作（compress / restore）の直前に必ず通す検査。
///
/// 方針: 書き込み系は fail-closed。判定できないものは「安全」と扱わず Block する。
/// （scan 側の NTFS 判定が fail-open なのとは意図的に逆。読むだけなら誤検出の害が小さいが、
/// 書く場合は誤検出の害が大きい。）
///
/// このクラス自身は一切書き込みを行わない。
/// </summary>
public sealed class PreFlightChecker(
    IFileSystem fs,
    Func<IReadOnlyCollection<string>>? runningProcessNames = null,
    Func<string, bool>? isFileInUse = null,
    PreFlightOptions? options = null)
{
    private readonly IFileSystem _fs = fs;
    private readonly PreFlightOptions _options = options ?? new PreFlightOptions();

    // プロセス列挙・ファイルロック検査は OS 依存なのでデリゲート注入。
    // 注入されなければ「判定できない」= 安全側（Block）に倒す。
    private readonly Func<IReadOnlyCollection<string>>? _runningProcessNames = runningProcessNames;
    private readonly Func<string, bool>? _isFileInUse = isFileInUse;

    public PreFlightReport Check(InstalledApp app, CancellationToken ct = default)
    {
        var issues = new List<PreFlightIssue>();

        CheckContainment(app, issues);
        CheckManifest(app, issues);

        var targetExists = _fs.DirectoryExists(app.FullPath);
        if (!targetExists)
        {
            issues.Add(new PreFlightIssue
            {
                CheckId = PreFlightCheckId.TargetExists,
                Blocks = true,
                Detail = app.FullPath,
            });

            // フォルダが無ければ以降のファイル系検査は無意味
            return new PreFlightReport { Issues = issues };
        }

        CheckFileSystem(app, issues);
        CheckReparsePoints(app, issues, ct);
        CheckSteamRunning(issues);
        CheckGameRunning(app, issues, ct);
        CheckDirectStorage(app, issues, ct);
        CheckSizeAndFreeSpace(app, issues, ct);

        return new PreFlightReport { Issues = issues };
    }

    // -----------------------------------------------------------------
    // 対象がライブラリ配下か
    // -----------------------------------------------------------------

    private void CheckContainment(InstalledApp app, List<PreFlightIssue> issues)
    {
        // installdir は「steamapps\common 直下のフォルダ名」でなければならない。
        // 区切り文字・相対参照・ドライブ指定を含む値は、manifest の破損か改ざん。
        if (!IsSafeInstallDir(app.InstallDir))
        {
            issues.Add(new PreFlightIssue
            {
                CheckId = PreFlightCheckId.TargetInsideLibrary,
                Blocks = true,
                Detail = app.InstallDir,
            });
            return;
        }

        // FullPath が実際に <library>\steamapps\common\<installdir> になっているかを突き合わせる
        var expected = _fs.Combine(app.Library.SteamAppsPath, "common", app.InstallDir);
        if (!string.Equals(
                NormalizeForComparison(app.FullPath),
                NormalizeForComparison(expected),
                StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new PreFlightIssue
            {
                CheckId = PreFlightCheckId.TargetInsideLibrary,
                Blocks = true,
                Detail = app.FullPath,
            });
        }
    }

    /// <summary>installdir として安全な値か（単一のフォルダ名であること）。</summary>
    public static bool IsSafeInstallDir(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir)) return false;
        if (installDir.Contains('/') || installDir.Contains('\\')) return false;
        if (installDir.Contains(':')) return false;
        if (installDir is "." or "..") return false;
        if (installDir.Trim() != installDir) return false;

        return true;
    }

    private static string NormalizeForComparison(string path)
        => path.Replace('/', '\\').TrimEnd('\\');

    // -----------------------------------------------------------------
    // manifest の存在（TOCTOU）
    // -----------------------------------------------------------------

    private void CheckManifest(InstalledApp app, List<PreFlightIssue> issues)
    {
        var manifest = _fs.Combine(app.Library.SteamAppsPath, $"appmanifest_{app.AppId}.acf");

        if (!_fs.FileExists(manifest))
        {
            issues.Add(new PreFlightIssue
            {
                CheckId = PreFlightCheckId.ManifestStillExists,
                Blocks = true,
                Detail = manifest,
            });
        }
    }

    // -----------------------------------------------------------------
    // NTFS
    // -----------------------------------------------------------------

    private void CheckFileSystem(InstalledApp app, List<PreFlightIssue> issues)
    {
        var name = _fs.GetFileSystemName(app.FullPath);

        // scan は判定不能を NTFS とみなす（fail-open）が、書き込み前は逆に倒す
        if (name is null || !name.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new PreFlightIssue
            {
                CheckId = PreFlightCheckId.NtfsFileSystem,
                Blocks = true,
                Detail = name ?? "(判定不能)",
            });
        }
    }

    // -----------------------------------------------------------------
    // reparse point（親と子。圧縮済み判定には使わない — D-005）
    // -----------------------------------------------------------------

    private void CheckReparsePoints(InstalledApp app, List<PreFlightIssue> issues, CancellationToken ct)
    {
        // 親方向: 対象フォルダからライブラリルートまで
        var libraryRoot = NormalizeForComparison(app.Library.Path);
        var current = NormalizeForComparison(app.FullPath);

        while (current.Length >= libraryRoot.Length && current.Contains('\\'))
        {
            ct.ThrowIfCancellationRequested();

            if (_fs.IsReparsePoint(current))
            {
                issues.Add(new PreFlightIssue
                {
                    CheckId = PreFlightCheckId.NoReparsePoint,
                    Blocks = true,
                    Detail = current,
                });
                return;
            }

            if (string.Equals(current, libraryRoot, StringComparison.OrdinalIgnoreCase)) break;

            var idx = current.LastIndexOf('\\');
            if (idx <= 0) break;
            current = current[..idx];
        }

        // 子方向: 幅優先で全サブディレクトリ（上限つき）
        var queue = new Queue<string>();
        queue.Enqueue(app.FullPath);
        var inspected = 0;

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var dir = queue.Dequeue();

            foreach (var sub in _fs.EnumerateDirectories(dir))
            {
                if (++inspected > _options.MaxDirectoriesToInspect)
                {
                    // 走査しきれないものを「安全」と扱わない
                    issues.Add(new PreFlightIssue
                    {
                        CheckId = PreFlightCheckId.NoReparsePoint,
                        Blocks = true,
                        Detail = "(サブディレクトリが多すぎて検査しきれません)",
                    });
                    return;
                }

                if (_fs.IsReparsePoint(sub))
                {
                    issues.Add(new PreFlightIssue
                    {
                        CheckId = PreFlightCheckId.NoReparsePoint,
                        Blocks = true,
                        Detail = sub,
                    });
                    return;
                }

                queue.Enqueue(sub);
            }
        }
    }

    // -----------------------------------------------------------------
    // Steam クライアント / ゲームの起動中
    // -----------------------------------------------------------------

    private void CheckSteamRunning(List<PreFlightIssue> issues)
    {
        if (_runningProcessNames is null)
        {
            issues.Add(new PreFlightIssue
            {
                CheckId = PreFlightCheckId.SteamNotRunning,
                Blocks = true,
                Detail = "(プロセス一覧を取得できません)",
            });
            return;
        }

        var running = _runningProcessNames();

        foreach (var name in running)
        {
            var lower = name.ToLowerInvariant();
            if (_options.SteamProcessNames.Contains(lower))
            {
                issues.Add(new PreFlightIssue
                {
                    CheckId = PreFlightCheckId.SteamNotRunning,
                    Blocks = true,
                    Detail = name,
                });
                return;
            }
        }
    }

    private void CheckGameRunning(InstalledApp app, List<PreFlightIssue> issues, CancellationToken ct)
    {
        if (_isFileInUse is null)
        {
            issues.Add(new PreFlightIssue
            {
                CheckId = PreFlightCheckId.GameNotRunning,
                Blocks = true,
                Detail = "(ファイル使用中の検査ができません)",
            });
            return;
        }

        foreach (var file in _fs.EnumerateFilesRecursive(app.FullPath))
        {
            ct.ThrowIfCancellationRequested();

            if (!_fs.GetExtension(file).Equals(".exe", StringComparison.OrdinalIgnoreCase)) continue;

            if (_isFileInUse(file))
            {
                issues.Add(new PreFlightIssue
                {
                    CheckId = PreFlightCheckId.GameNotRunning,
                    Blocks = true,
                    Detail = _fs.GetFileName(file),
                });
                return;
            }
        }
    }

    // -----------------------------------------------------------------
    // DirectStorage の兆候
    // -----------------------------------------------------------------

    private void CheckDirectStorage(InstalledApp app, List<PreFlightIssue> issues, CancellationToken ct)
    {
        var detector = new FeatureDetector();

        foreach (var file in _fs.EnumerateFilesRecursive(app.FullPath))
        {
            ct.ThrowIfCancellationRequested();
            detector.Feed(_fs.GetFileName(file));
        }

        if (detector.Result.HasFlag(GameFeatures.DirectStorage))
        {
            // 技術的に不可能なわけではないので Warn。
            // 逆に「兆候が無い」ことをどこにも「安全」とは表示しないこと（AGENTS.md）
            issues.Add(new PreFlightIssue
            {
                CheckId = PreFlightCheckId.DirectStorageSigns,
                Blocks = false,
                Detail = "dstorage*.dll",
            });
        }
    }

    // -----------------------------------------------------------------
    // 容量の測定と空き容量
    // -----------------------------------------------------------------

    private void CheckSizeAndFreeSpace(InstalledApp app, List<PreFlightIssue> issues, CancellationToken ct)
    {
        long total = 0;
        long largest = 0;
        var anyFile = false;

        foreach (var file in _fs.EnumerateFilesRecursive(app.FullPath))
        {
            ct.ThrowIfCancellationRequested();

            anyFile = true;
            var size = _fs.GetFileSize(file);
            total += size;
            if (size > largest) largest = size;
        }

        if (!anyFile || total <= 0)
        {
            issues.Add(new PreFlightIssue
            {
                CheckId = PreFlightCheckId.SizeMeasurable,
                Blocks = true,
                Detail = app.FullPath,
            });
            return;
        }

        var free = _fs.GetAvailableFreeBytes(app.FullPath);
        var required = largest + _options.FreeSpaceMarginBytes;

        if (free is null || free < required)
        {
            issues.Add(new PreFlightIssue
            {
                CheckId = PreFlightCheckId.EnoughFreeSpace,
                Blocks = true,
                Detail = free is { } f
                    ? $"free={f} required={required}"
                    : "(空き容量を判定できません)",
            });
        }
    }
}
