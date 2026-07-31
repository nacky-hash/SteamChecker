using System.Text;
using System.Text.Json;
using SteamChecker.Core;
using SteamChecker.Core.Analysis;
using SteamChecker.Core.Compression;
using SteamChecker.Core.Presentation;
using SteamChecker.Core.Steam;

Console.OutputEncoding = Encoding.UTF8;

var command = args.FirstOrDefault() ?? "scan";

if (command is "-h" or "--help" or "help")
{
    PrintUsage();
    return 0;
}

var fs = new PhysicalFileSystem();
var steamRoot = GetOption("--steam") ?? new SteamLocator(fs, LocateSteamFromRegistry).Locate();

if (steamRoot is null)
{
    Error("Steam のインストール先が見つかりませんでした。--steam <パス> で指定してください。");
    return 1;
}

return command switch
{
    "scan" => RunScan(),
    "compress" => await RunCompressAsync(compress: true),
    "restore" => await RunCompressAsync(compress: false),
    "history" => RunHistory(),
    _ => UnknownCommand(command),
};

// =====================================================================
// scan
// =====================================================================

int RunScan()
{
    var json = HasFlag("--json");
    var showAll = HasFlag("--all");

    var defaults = new AdvisorOptions();

    var options = new AdvisorOptions
    {
        UninstallAdvice = HasFlag("--no-uninstall-advice")
            ? UninstallAdviceMode.Off
            : UninstallAdviceMode.Subtle,

        // 閾値は環境で妥当な値が変わる。256GB の SSD しかない人にとっては
        // 「1GB しか空かない」も十分な成果なので、決め打ちにはしない
        MinSavedBytes = MegabytesOption("--min-saved-mb") ?? defaults.MinSavedBytes,
        MinSavedFraction = PercentOption("--min-saved-percent") ?? defaults.MinSavedFraction,
        LongUnplayedDays = IntOption("--unplayed-days") ?? defaults.LongUnplayedDays,
        UninstallMinBytes = MegabytesOption("--uninstall-min-mb") ?? defaults.UninstallMinBytes,
        LargeSavingBytes = MegabytesOption("--large-saving-mb") ?? defaults.LargeSavingBytes,
    };

    var scanner = new LibraryScanner(fs, options);

    ScanResult result;

    if (json)
    {
        result = scanner.Scan(steamRoot, assumeNtfs: HasFlag("--assume-ntfs"));
    }
    else
    {
        Console.WriteLine($"Steam: {steamRoot}");
        Console.WriteLine();

        var progress = new Progress<ScanProgress>(p =>
        {
            if (p.CurrentTitle is null) return;
            var text = $"  解析中 [{p.Completed + 1}/{p.Total}] {p.CurrentTitle}";
            Console.Write("\r" + text.PadRight(Math.Min(Console.IsOutputRedirected ? 80 : Console.WindowWidth - 1, 100)));
        });

        result = scanner.Scan(steamRoot, progress, assumeNtfs: HasFlag("--assume-ntfs"));
        Console.Write("\r" + new string(' ', 100) + "\r");
    }

    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            result.Assessments.Select(a => new
            {
                a.AppId,
                a.Name,
                a.InstallPath,
                Advice = a.Advice.ToString(),
                Reasons = a.Reasons.Select(r => r.ToString()),
                a.SizeBytes,
                EstimatedSavedBytes = a.Estimate.EstimatedSavedBytes,
                CompressionRatio = Math.Round(a.Estimate.Ratio, 4),
                Measured = a.Estimate.Measured,
                a.DaysSincePlayed,
                a.DaysSinceUpdated,
                a.IsUninstallCandidate,
                Features = a.Features.ToString(),
            }),
            new JsonSerializerOptions { WriteIndented = true }));

        return 0;
    }

    PrintReport(result, showAll);
    return 0;
}

void PrintReport(ScanResult result, bool showAll)
{
    if (result.Assessments.Count == 0)
    {
        Console.WriteLine("インストール済みのゲームが見つかりませんでした。");
        return;
    }

    Console.WriteLine($"ライブラリ {result.Libraries.Count} 件 / タイトル {result.Assessments.Count} 件");

    if (result.PlayHistoryUnavailable)
    {
        Warn("プレイ履歴を読めませんでした。未起動判定は行われていません。");
    }

    Console.WriteLine();

    var groups = new[]
    {
        (AdviceKind.Compress, ConsoleColor.Green),
        (AdviceKind.CompressUpdatesOften, ConsoleColor.Cyan),
        (AdviceKind.CompressAntiCheat, ConsoleColor.Yellow),
        (AdviceKind.NotWorthCompressing, ConsoleColor.DarkGray),
        (AdviceKind.DoNotCompress, ConsoleColor.DarkYellow),
        (AdviceKind.AlreadyCompressed, ConsoleColor.DarkGray),
    };

    foreach (var (kind, color) in groups)
    {
        var items = result.Assessments.Where(a => a.Advice == kind).ToList();
        if (items.Count == 0) continue;

        // 効果が無いものは既定で畳む。全部見たいときは --all
        var hidden = 0;
        if (!showAll && kind is AdviceKind.NotWorthCompressing or AdviceKind.AlreadyCompressed)
        {
            var keep = items.Where(a => a.IsUninstallCandidate).ToList();
            hidden = items.Count - keep.Count;
            items = keep;
        }

        if (items.Count == 0)
        {
            if (hidden > 0) Console.WriteLine($"■ {AdviceFormatter.Label(kind)}  ({hidden} 件 省略 / --all で表示)");
            continue;
        }

        WriteColor(color, $"■ {AdviceFormatter.Label(kind)}  ({items.Count} 件)");

        foreach (var a in items)
        {
            Console.WriteLine($"   {a.Name}");
            Console.WriteLine($"     {AdviceFormatter.OneLineSummary(a)}");

            foreach (var reason in a.Reasons.Where(IsWorthShowing))
            {
                Console.WriteLine($"     · {AdviceFormatter.Describe(reason, a)}");
            }

            Console.WriteLine($"     appid {a.AppId}   {a.InstallPath}");
            Console.WriteLine();
        }

        if (hidden > 0) Console.WriteLine($"   （ほか {hidden} 件は効果が小さいため省略 / --all で表示）\n");
    }

    Console.WriteLine(new string('-', 70));
    Console.WriteLine($"インストール合計       {AdviceFormatter.Bytes(result.TotalSizeBytes)}");
    Console.WriteLine($"圧縮で空く見込み       {AdviceFormatter.Bytes(result.TotalEstimatedSavingBytes)}");

    if (result.UninstallCandidateBytes > 0)
    {
        Console.WriteLine($"長期未起動タイトル     {AdviceFormatter.Bytes(result.UninstallCandidateBytes)}"
                          + "（圧縮より削除のほうが多く空くもの。このツールは削除しません）");
    }

    Console.WriteLine();
    Console.WriteLine("このコマンドはファイルを一切変更していません。");
    Console.WriteLine("圧縮の実行は現在 --experimental が必要です（Phase 0 の範囲外のため）。");
}

static bool IsWorthShowing(ReasonCode code) => code is not ReasonCode.RecentlyPlayed;

// =====================================================================
// compress / restore
// =====================================================================

async Task<int> RunCompressAsync(bool compress)
{
    // Phase 0（読み取り専用）を初回リリースの範囲としているため、
    // 書き込みを伴う操作は明示的なオプトインを必須にしている。
    //
    // 動作自体は Windows 11 実機で検証済み（docs/RESEARCH.md）:
    //   · 圧縮→解除の往復を実ゲーム6プロファイルで実行し、全て元サイズに完全復帰
    //   · 見積もり精度の補正係数は k=1.0001（補正不要）
    // それでも既定で塞いでいるのは、初回は「解析だけするツール」として
    // 信頼を得ることを優先しているため。機能不足ではなく範囲の選択。
    if (!HasFlag("--experimental"))
    {
        Error("compress / restore は初回リリースの範囲外です。");
        Console.WriteLine();
        Console.WriteLine("  このツールは現在 Phase 0（解析のみ・書き込みなし）として提供しています。");
        Console.WriteLine("  圧縮機能は実装済みで Windows 実機での往復検証も通っていますが、");
        Console.WriteLine("  既定では実行しません。");
        Console.WriteLine();
        Console.WriteLine("  試すなら --experimental を付けてください。");
        Console.WriteLine("  書き込まずに動作だけ見るなら --dry-run が使えます。");
        return 1;
    }

    var engine = new CompactExeEngine(fs, dryRun: HasFlag("--dry-run"));

    if (!engine.IsAvailable && !HasFlag("--dry-run"))
    {
        Error("compact.exe が使えません。この操作は Windows でのみ実行できます。");
        Console.WriteLine("（--dry-run を付ければ他の OS でも動作確認だけはできます）");
        return 1;
    }

    var journal = new OperationJournal(GetOption("--journal") ?? OperationJournal.DefaultPath);

    if (!compress && HasFlag("--all"))
    {
        var paths = journal.CompressedPaths();
        if (paths.Count == 0)
        {
            Console.WriteLine("圧縮済みとして記録されているフォルダはありません。");
            return 0;
        }

        // ジャーナルはユーザー領域の書き換え可能なファイルなので、
        // 記録されているパスを無条件に信用しない。
        // 現在検出できる Steam ライブラリの common 配下だけを対象にする
        var allowedRoots = new SteamReader(fs).ReadLibraries(steamRoot)
            .Select(lib => lib.SteamAppsPath.TrimEnd('\\', '/') + @"\common\")
            .ToList();

        var (inside, outside) = (new List<string>(), new List<string>());
        foreach (var path in paths)
        {
            var normalized = path.Replace('/', '\\');
            if (allowedRoots.Any(root => normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                inside.Add(path);
            else
                outside.Add(path);
        }

        foreach (var path in outside)
        {
            Warn($"Steam ライブラリ配下ではないため飛ばします: {path}");
        }

        if (inside.Count == 0)
        {
            Console.WriteLine("復元対象がありません。");
            return 0;
        }

        Console.WriteLine($"{inside.Count} 件を元に戻します。");

        foreach (var path in inside)
        {
            await ExecuteAsync(engine, journal, appId: 0, name: path, path, compress: false);
        }

        return 0;
    }

    var target = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
    if (target is null)
    {
        Error("appid を指定してください。例: steamchecker compress 1091500");
        return 1;
    }

    if (!long.TryParse(target, out var appId))
    {
        Error($"appid が数値ではありません: {target}");
        return 1;
    }

    var reader = new SteamReader(fs);
    var app = reader.ReadLibraries(steamRoot)
        .SelectMany(reader.ReadInstalledApps)
        .FirstOrDefault(a => a.AppId == appId);

    if (app is null)
    {
        Error($"appid {appId} のインストールが見つかりませんでした。");
        return 1;
    }

    // --- 書き込みの前に必ず事前検査を通す（fail-closed） ---
    var preFlight = new PreFlightChecker(
        fs,
        runningProcessNames: () => System.Diagnostics.Process.GetProcesses()
            .Select(p => { try { return p.ProcessName; } catch { return string.Empty; } })
            .Where(n => n.Length > 0)
            .ToList(),
        isFileInUse: FileLockProbe.IsFileInUse);

    var report = preFlight.Check(app);

    foreach (var issue in report.Issues)
    {
        if (issue.Blocks) Error(PreFlightFormatter.Describe(issue));
        else Warn(PreFlightFormatter.Describe(issue));
    }

    if (!report.CanProceed)
    {
        Console.WriteLine();
        Console.WriteLine("事前検査で問題が見つかったため実行しません。ファイルは変更していません。");
        return 1;
    }

    // 圧縮前に必ず現状を測って見せる。「実行したら何が起きるか」を伏せない
    if (compress)
    {
        var profile = new FolderProfiler(fs).Profile(app.FullPath);
        var estimate = new SamplingEstimator(fs).Estimate(profile);

        Console.WriteLine($"{app.Name}");
        Console.WriteLine($"  パス      {app.FullPath}");
        Console.WriteLine($"  サイズ    {AdviceFormatter.Bytes(profile.TotalLogicalBytes)}");
        Console.WriteLine($"  見込み    {AdviceFormatter.Bytes(estimate.EstimatedSavedBytes)} 削減 "
                          + $"({estimate.SavedFraction:P0}){(estimate.Measured ? " ※実測" : " ※推定")}");

        if (profile.Features.HasFlag(GameFeatures.DirectStorage))
        {
            Warn("DirectStorage 対応タイトルです。圧縮すると読み込みが遅くなる可能性があります。");
        }

        if (profile.Features.HasAnyAntiCheat())
        {
            Warn("アンチチートを検出しました。動作に問題が出た場合は restore で元に戻せます。");
        }

        Console.WriteLine();

        if (!HasFlag("--yes") && !Confirm("実行しますか?")) return 0;
    }

    return await ExecuteAsync(engine, journal, app.AppId, app.Name, app.FullPath, compress);
}

async Task<int> ExecuteAsync(
    ICompressionEngine engine,
    OperationJournal journal,
    long appId,
    string name,
    string path,
    bool compress)
{
    var algorithm = ParseAlgorithm(GetOption("--algorithm"));
    var lastReport = 0;

    var progress = new Progress<CompressionProgress>(p =>
    {
        if (p.FilesProcessed - lastReport < 50) return;
        lastReport = p.FilesProcessed;
        Console.Write($"\r  {p.FilesProcessed} ファイル処理".PadRight(60));
    });

    // Ctrl+C で compact.exe ごと止める（放置すると中断したつもりで圧縮が進み続ける）
    using var cts = new CancellationTokenSource();
    ConsoleCancelEventHandler onCancel = (_, e) =>
    {
        e.Cancel = true; // 即死せず、キャンセル処理（子プロセス停止と記録）を通す
        cts.Cancel();
    };
    Console.CancelKeyPress += onCancel;

    CompressionResult result;
    try
    {
        result = compress
            ? await engine.CompressAsync(path, algorithm, progress, cts.Token)
            : await engine.DecompressAsync(path, progress, cts.Token);
    }
    finally
    {
        Console.CancelKeyPress -= onCancel;
    }

    Console.Write("\r" + new string(' ', 60) + "\r");

    try
    {
        journal.Record(compress ? "compress" : "decompress", appId, name, result, compress ? algorithm : null);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        // 操作自体は完了している。記録できなかった事実は隠さず伝え、結果報告は続ける
        Warn($"操作ログを書き込めませんでした: {ex.Message}");
        Warn($"記録先: {journal.FilePath}");
    }

    if (!result.Success)
    {
        Error($"失敗: {result.ErrorMessage}");
        return 1;
    }

    if (result.WasDryRun)
    {
        // 「完了」と偽らない。何も書き込んでいない事実をそのまま出す
        Console.WriteLine($"dry-run: {name} には何も書き込んでいません（実行時の対象 {AdviceFormatter.Bytes(result.BytesBefore)}）。");
        return 0;
    }

    var verb = compress ? "圧縮" : "復元";
    WriteColor(ConsoleColor.Green,
        $"{verb}完了: {name}  "
        + $"{AdviceFormatter.Bytes(result.BytesBefore)} → {AdviceFormatter.Bytes(result.BytesAfter)} "
        + $"({(result.BytesSaved >= 0 ? "-" : "+")}{AdviceFormatter.Bytes(Math.Abs(result.BytesSaved))})");

    Console.WriteLine($"  所要 {result.Duration.TotalSeconds:F1} 秒 / 記録先 {journal.FilePath}");
    return 0;
}

// =====================================================================
// history
// =====================================================================

int RunHistory()
{
    var journal = new OperationJournal(GetOption("--journal") ?? OperationJournal.DefaultPath);
    var entries = journal.ReadAll();

    if (entries.Count == 0)
    {
        Console.WriteLine("操作履歴はありません。");
        return 0;
    }

    foreach (var e in entries)
    {
        var status = e.Success ? "OK  " : "NG  ";
        var delta = e.BytesBefore - e.BytesAfter;

        Console.WriteLine(
            $"{e.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm}  {status}"
            + $"{e.Operation,-10} {e.Name}  "
            + $"{AdviceFormatter.Bytes(e.BytesBefore)} → {AdviceFormatter.Bytes(e.BytesAfter)} "
            + $"({(delta >= 0 ? "-" : "+")}{AdviceFormatter.Bytes(Math.Abs(delta))})");

        if (e.ErrorMessage is { Length: > 0 }) Console.WriteLine($"    {e.ErrorMessage}");
    }

    Console.WriteLine();
    Console.WriteLine($"現在圧縮済みとして記録されているフォルダ: {journal.CompressedPaths().Count} 件");
    return 0;
}

// =====================================================================
// ヘルパー
// =====================================================================

int UnknownCommand(string name)
{
    Error($"不明なコマンド: {name}");
    PrintUsage();
    return 1;
}

void PrintUsage()
{
    Console.WriteLine("""
        steamchecker — Steam ライブラリの容量アドバイザー

        主コマンド:
          steamchecker scan [--all] [--json] [--no-uninstall-advice]
              ライブラリを解析し、タイトルごとの推奨を表示します。
              ファイルへの書き込みは一切行いません。

          steamchecker history
              これまでの操作履歴を表示します。

        初回リリース範囲外（--experimental が必要 / 実機検証は済み）:
          steamchecker compress <appid> --experimental [--algorithm LZX|XPRESS16K|XPRESS8K|XPRESS4K] [--yes]
          steamchecker restore  <appid> --experimental
          steamchecker restore  --all   --experimental
              Phase 0 の範囲外のため、既定では実行できません。

        共通オプション:
          --steam <パス>     Steam のインストール先を明示する
          --journal <パス>   操作ログの保存先を変える
          --dry-run          実際には書き込まない
          --assume-ntfs      ファイルシステム判定を飛ばす（Windows 以外での動作確認用）

        scan の閾値調整:
          --min-saved-mb <数>       この量未満しか空かないなら「効果小」（既定 1024）
          --min-saved-percent <数>  この割合未満しか縮まないなら「効果小」（既定 10）
          --unplayed-days <数>      長期未起動とみなす日数（既定 180）
          --uninstall-min-mb <数>   削除候補として扱う最小サイズ（既定 20480）
          --large-saving-mb <数>    「大きく空く」と表示する閾値（既定 5120）

        注意:
          · 圧縮はファイルへの書き込みで自動的に解除されます。ゲーム更新のたびに再圧縮が必要です。
          · DirectStorage 対応タイトルは既定で対象外にしています。
          · NTFS 以外のドライブでは動作しません。
        """);
}

bool HasFlag(string flag) => args.Contains(flag, StringComparer.OrdinalIgnoreCase);

int? IntOption(string name)
    => int.TryParse(GetOption(name), out var v) && v >= 0 ? v : null;

long? MegabytesOption(string name)
    => double.TryParse(GetOption(name), out var v) && v >= 0 ? (long)(v * 1024 * 1024) : null;

double? PercentOption(string name)
    => double.TryParse(GetOption(name), out var v) && v is >= 0 and <= 100 ? v / 100.0 : null;

string? GetOption(string name)
{
    var index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static CompressionAlgorithm ParseAlgorithm(string? value) => value?.ToUpperInvariant() switch
{
    "XPRESS4K" => CompressionAlgorithm.Xpress4K,
    "XPRESS8K" => CompressionAlgorithm.Xpress8K,
    "XPRESS16K" => CompressionAlgorithm.Xpress16K,
    _ => CompressionAlgorithm.Lzx,
};

static bool Confirm(string question)
{
    Console.Write($"{question} [y/N] ");
    var answer = Console.ReadLine();
    return answer is not null && answer.Trim().StartsWith('y');
}

static void WriteColor(ConsoleColor color, string text)
{
    var previous = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine(text);
    Console.ForegroundColor = previous;
}

static void Error(string text) => WriteColor(ConsoleColor.Red, "エラー: " + text);

static void Warn(string text) => WriteColor(ConsoleColor.Yellow, "注意: " + text);

/// <summary>
/// Windows のレジストリから Steam のパスを読む。
/// Core は OS 非依存に保ちたいので、レジストリ依存はここ（アプリ層）に閉じ込める。
/// </summary>
static string? LocateSteamFromRegistry()
{
    if (!OperatingSystem.IsWindows()) return null;

    // Microsoft.Win32.Registry への参照を Core に持ち込まないため、
    // reg.exe の出力を読む。起動コストは 1 回だけなので実用上問題ない。
    try
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            // PATH / カレントディレクトリの reg.exe を拾わないようフルパスで指定する
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe"),
            Arguments = @"query HKCU\Software\Valve\Steam /v SteamPath",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        if (process is null) return null;

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);

        var line = output.Split('\n').FirstOrDefault(l => l.Contains("SteamPath"));
        if (line is null) return null;

        var index = line.IndexOf("REG_SZ", StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;

        return line[(index + 6)..].Trim().Replace('/', '\\');
    }
    catch
    {
        return null;
    }
}
