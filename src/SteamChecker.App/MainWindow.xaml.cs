using System.IO;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;
using SteamChecker.Core;
using SteamChecker.Core.Analysis;
using SteamChecker.Core.Compression;
using SteamChecker.Core.Presentation;
using SteamChecker.Core.Steam;

namespace SteamChecker.App;

/// <summary>
/// 一覧表示のための行データ。表示用文字列と、操作対象を特定する AppId だけを持つ。
/// 判定は Core（LibraryScanner / Advisor）が済ませたものをそのまま並べる。
/// </summary>
public sealed record ResultRow(long AppId, string Group, string Title, string Summary, string Reasons);

public partial class MainWindow : Window
{
    private readonly PhysicalFileSystem _fs = new();
    private string? _steamRoot;
    private CancellationTokenSource? _operationCts;

    public MainWindow()
    {
        InitializeComponent();

        // CLI の --experimental と同じ意味の起動引数を受け付ける
        if (Environment.GetCommandLineArgs().Contains("--experimental", StringComparer.OrdinalIgnoreCase))
        {
            ExperimentalCheck.IsChecked = true;
        }
    }

    // =================================================================
    // スキャン（読み取り専用）
    // =================================================================

    private async void OnScanClick(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        ResultList.ItemsSource = null;
        SummaryText.Text = string.Empty;

        try
        {
            _steamRoot ??= new SteamLocator(_fs, LocateSteamFromRegistry).Locate();

            if (_steamRoot is null)
            {
                StatusText.Text = "Steam のインストール先が見つかりませんでした。";
                return;
            }

            var progress = new Progress<ScanProgress>(p =>
                StatusText.Text = p.CurrentTitle is null
                    ? $"解析完了 ({p.Completed}/{p.Total})"
                    : $"解析中 [{p.Completed + 1}/{p.Total}] {p.CurrentTitle}");

            // 判定は全て Core 側。UI スレッドを塞がないよう別スレッドで回す
            var scanner = new LibraryScanner(_fs);
            var steamRoot = _steamRoot;
            var result = await Task.Run(() => scanner.Scan(steamRoot, progress));

            ShowResult(result);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"エラー: {ex.Message}";
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }
    }

    private void ShowResult(ScanResult result)
    {
        var rows = result.Assessments
            .Select(a => new ResultRow(
                AppId: a.AppId,
                Group: AdviceFormatter.Label(a.Advice),
                Title: a.Name,
                Summary: AdviceFormatter.OneLineSummary(a),
                Reasons: string.Join(" / ",
                    a.Reasons
                        .Where(r => r != ReasonCode.RecentlyPlayed)
                        .Select(r => AdviceFormatter.Describe(r, a)))))
            .ToList();

        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ResultRow.Group)));
        ResultList.ItemsSource = view;

        SummaryText.Text =
            $"タイトル {result.Assessments.Count} 件 / 合計 {AdviceFormatter.Bytes(result.TotalSizeBytes)} / "
            + $"圧縮で空く見込み {AdviceFormatter.Bytes(result.TotalEstimatedSavingBytes)}";

        StatusText.Text = result.PlayHistoryUnavailable
            ? "解析完了（プレイ履歴を読めなかったため、未起動判定は行われていません）"
            : "解析完了";
    }

    // =================================================================
    // 圧縮 / 復元（実験的。CLI の --experimental と同じゲート）
    // =================================================================

    private void OnExperimentalChanged(object sender, RoutedEventArgs e) => UpdateActionButtons();

    private void OnSelectionChanged(object sender, RoutedEventArgs e)
    {
        SelectionText.Text = ResultList.SelectedItem is ResultRow row
            ? $"{row.Title} — {row.Summary}"
            : "タイトルを選択すると操作できます（圧縮機能が有効な場合）";

        UpdateActionButtons();
    }

    private void UpdateActionButtons()
    {
        var ready = ExperimentalCheck.IsChecked == true
                    && ResultList.SelectedItem is ResultRow
                    && _operationCts is null;

        CompressButton.IsEnabled = ready;
        RestoreButton.IsEnabled = ready;
    }

    private async void OnCompressClick(object sender, RoutedEventArgs e) => await RunOperationAsync(compress: true);

    private async void OnRestoreClick(object sender, RoutedEventArgs e) => await RunOperationAsync(compress: false);

    private void OnCancelClick(object sender, RoutedEventArgs e) => _operationCts?.Cancel();

    private async Task RunOperationAsync(bool compress)
    {
        if (ResultList.SelectedItem is not ResultRow row || _steamRoot is null) return;

        // スキャン結果は古くなっている可能性があるので、操作直前に manifest から取り直す
        // （CLI と同じ流れ。ここで取れなければ TOCTOU として中止）
        var reader = new SteamReader(_fs);
        var app = reader.ReadLibraries(_steamRoot)
            .SelectMany(reader.ReadInstalledApps)
            .FirstOrDefault(a => a.AppId == row.AppId);

        if (app is null)
        {
            MessageBox.Show(this,
                "このタイトルのインストールが見つかりませんでした。スキャン後に移動・削除された可能性があります。",
                "実行できません", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // --- 事前検査（fail-closed）。判定は Core 側 ---
        var preFlight = new PreFlightChecker(
            _fs,
            runningProcessNames: () => System.Diagnostics.Process.GetProcesses()
                .Select(p => { try { return p.ProcessName; } catch { return string.Empty; } })
                .Where(n => n.Length > 0)
                .ToList(),
            isFileInUse: FileLockProbe.IsFileInUse);

        var report = await Task.Run(() => preFlight.Check(app));

        if (!report.CanProceed)
        {
            var reasons = string.Join("\n", report.Issues.Where(i => i.Blocks).Select(PreFlightFormatter.Describe));
            MessageBox.Show(this,
                $"事前検査で問題が見つかったため実行しません。ファイルは変更していません。\n\n{reasons}",
                "実行できません", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // --- 実行前の確認。何が起きるかを伏せない（CLI の確認と同等） ---
        var warnings = report.Issues.Where(i => !i.Blocks).Select(PreFlightFormatter.Describe).ToList();
        string confirmText;

        if (compress)
        {
            var profile = await Task.Run(() => new FolderProfiler(_fs).Profile(app.FullPath));
            var estimate = await Task.Run(() => new SamplingEstimator(_fs).Estimate(profile));

            confirmText =
                $"{app.Name} を圧縮します。\n\n"
                + $"サイズ      {AdviceFormatter.Bytes(profile.TotalLogicalBytes)}\n"
                + $"削減見込み  {AdviceFormatter.Bytes(estimate.EstimatedSavedBytes)}"
                + $" ({estimate.SavedFraction:P0}){(estimate.Measured ? " ※実測に基づく推定" : " ※推定")}\n\n"
                + "圧縮は restore でいつでも元に戻せます。ゲーム更新の書き込みで自動的に解除されます。";
        }
        else
        {
            confirmText = $"{app.Name} の圧縮を解除して元に戻します。";
        }

        if (warnings.Count > 0)
        {
            confirmText += "\n\n注意:\n" + string.Join("\n", warnings);
        }

        var answer = MessageBox.Show(this, confirmText,
            compress ? "圧縮の確認" : "復元の確認",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK) return;

        // --- 実行 ---
        var engine = new CompactExeEngine(_fs);
        var journal = new OperationJournal(OperationJournal.DefaultPath);

        _operationCts = new CancellationTokenSource();
        SetBusy(true, compress ? "圧縮中" : "復元中", app.Name);

        try
        {
            var progress = new Progress<CompressionProgress>(p =>
                StatusText.Text = $"{(compress ? "圧縮中" : "復元中")}: {app.Name}  {p.FilesProcessed} ファイル処理");

            var result = compress
                ? await engine.CompressAsync(app.FullPath, CompressionAlgorithm.Lzx, progress, _operationCts.Token)
                : await engine.DecompressAsync(app.FullPath, progress, _operationCts.Token);

            try
            {
                journal.Record(compress ? "compress" : "decompress", app.AppId, app.Name, result,
                    compress ? CompressionAlgorithm.Lzx : null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, $"操作ログを書き込めませんでした: {ex.Message}\n記録先: {journal.FilePath}",
                    "ログ書き込み失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            if (result.Success)
            {
                StatusText.Text =
                    $"{(compress ? "圧縮" : "復元")}完了: {app.Name}  "
                    + $"{AdviceFormatter.Bytes(result.BytesBefore)} → {AdviceFormatter.Bytes(result.BytesAfter)}"
                    + "（表示を最新にするには再スキャンしてください）";
            }
            else
            {
                StatusText.Text = $"中断/失敗: {result.ErrorMessage}";
                MessageBox.Show(this,
                    $"{result.ErrorMessage}\n\n途中まで処理された状態も有効です。restore でいつでも元に戻せます。",
                    "完了しませんでした", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"エラー: {ex.Message}";
        }
        finally
        {
            _operationCts.Dispose();
            _operationCts = null;
            SetBusy(false, string.Empty, string.Empty);
        }
    }

    private void SetBusy(bool busy, string verb, string title)
    {
        ScanButton.IsEnabled = !busy;
        ExperimentalCheck.IsEnabled = !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) StatusText.Text = $"{verb}: {title}";
        UpdateActionButtons();
    }

    /// <summary>
    /// レジストリから Steam のパスを読む。
    /// Core にレジストリ依存を持ち込まないため、アプリ層からデリゲートで注入する（D-009）。
    /// </summary>
    private static string? LocateSteamFromRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") as string is { Length: > 0 } path
                ? path.Replace('/', '\\')
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
