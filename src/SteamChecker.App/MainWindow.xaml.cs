using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using SteamChecker.Core;
using SteamChecker.Core.Analysis;
using SteamChecker.Core.Compression;
using SteamChecker.Core.Presentation;
using SteamChecker.Core.Steam;

namespace SteamChecker.App;

/// <summary>
/// 一覧表示のための行データ。判定は Core が済ませたものをそのまま持つ。
/// ソートのために数値も保持する（表示用文字列だけだと辞書順になって意味がなくなる）。
/// </summary>
public sealed record ResultRow(
    long AppId,
    int GroupOrder,
    string Group,
    string Title,
    string SizeText,
    long SizeBytes,
    string SavingText,
    long SavedBytes,
    string PlayedText,
    int DaysSincePlayedSort,
    string Reasons);

public partial class MainWindow : Window
{
    private readonly PhysicalFileSystem _fs = new();
    private string? _steamRoot;
    private CancellationTokenSource? _operationCts;

    private string _sortColumn = string.Empty;
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;

    /// <summary>グループの並び順。ソートしてもこの順序は崩さない。</summary>
    private static readonly AdviceKind[] GroupOrderList =
    [
        AdviceKind.Compress,
        AdviceKind.CompressWithWatcher,
        AdviceKind.CompressWithCaution,
        AdviceKind.NotWorthCompressing,
        AdviceKind.DoNotCompress,
        AdviceKind.AlreadyCompressed,
    ];

    public MainWindow()
    {
        InitializeComponent();
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
                GroupOrder: Array.IndexOf(GroupOrderList, a.Advice),
                Group: AdviceFormatter.Label(a.Advice),
                Title: a.Name,
                SizeText: AdviceFormatter.Bytes(a.SizeBytes),
                SizeBytes: a.SizeBytes,
                SavingText: a.Advice == AdviceKind.AlreadyCompressed
                    ? $"圧縮済み {AdviceFormatter.Bytes(Math.Max(0, a.SizeBytes - a.PhysicalBytes))}"
                    : AdviceFormatter.Bytes(a.Estimate.EstimatedSavedBytes),
                SavedBytes: a.Advice == AdviceKind.AlreadyCompressed
                    ? Math.Max(0, a.SizeBytes - a.PhysicalBytes)
                    : a.Estimate.EstimatedSavedBytes,
                PlayedText: a.DaysSincePlayed is { } d
                    ? $"{AdviceFormatter.Duration(d)}前"
                    : "記録なし",
                // 起動記録なしは「最も古い」側に置く（未起動を上位に集めたい用途に合う）
                DaysSincePlayedSort: a.DaysSincePlayed ?? int.MaxValue,
                Reasons: string.Join(" / ",
                    a.Reasons
                        .Where(r => r != ReasonCode.RecentlyPlayed)
                        .Select(r => AdviceFormatter.Describe(r, a)))))
            .ToList();

        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ResultRow.Group)));
        ResultList.ItemsSource = view;

        ApplySort();

        SummaryText.Text =
            $"タイトル {result.Assessments.Count} 件 / 合計 {AdviceFormatter.Bytes(result.TotalSizeBytes)} / "
            + $"圧縮で空く見込み {AdviceFormatter.Bytes(result.TotalEstimatedSavingBytes)}";

        StatusText.Text = result.PlayHistoryUnavailable
            ? "解析完了（プレイ履歴を読めなかったため、未起動判定は行われていません）"
            : "解析完了";
    }

    // =================================================================
    // 並べ替え（大分類は崩さず、その中で並べ替える）
    // =================================================================

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Tag: string column }) return;

        if (_sortColumn == column)
        {
            _sortDirection = _sortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        }
        else
        {
            _sortColumn = column;
            // サイズ・削減量・未起動日数は「大きい順」が知りたい順序なので降順から始める
            _sortDirection = column is nameof(ResultRow.Title) or nameof(ResultRow.Reasons)
                ? ListSortDirection.Ascending
                : ListSortDirection.Descending;
        }

        ApplySort();
    }

    private void ApplySort()
    {
        if (ResultList.ItemsSource is not ListCollectionView view) return;

        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();

            // 第1キーは常にグループ順。これを外すと並べ替えでグループの並びまで変わる
            view.SortDescriptions.Add(
                new SortDescription(nameof(ResultRow.GroupOrder), ListSortDirection.Ascending));

            if (_sortColumn.Length > 0)
            {
                view.SortDescriptions.Add(new SortDescription(_sortColumn, _sortDirection));
            }
        }

        var arrow = _sortDirection == ListSortDirection.Ascending ? "▲" : "▼";
        StatusText.Text = _sortColumn.Length == 0
            ? StatusText.Text
            : $"並べ替え: {ColumnLabel(_sortColumn)} {arrow}（大分類の順序は固定）";
    }

    private static string ColumnLabel(string column) => column switch
    {
        nameof(ResultRow.Title) => "タイトル",
        nameof(ResultRow.SizeBytes) => "サイズ",
        nameof(ResultRow.SavedBytes) => "圧縮見込み",
        nameof(ResultRow.DaysSincePlayedSort) => "最終プレイ",
        nameof(ResultRow.Reasons) => "根拠",
        _ => column,
    };

    // =================================================================
    // 圧縮 / 復元
    //
    // 「圧縮機能を有効にする」チェックボックスは廃止した（D-015）。
    // 圧縮ツールで圧縮を有効化させる UI は意味が伝わらないうえ、
    // 実際の安全性はチェックボックスではなく
    //   事前検査(fail-closed) / 削減見込みを示す確認ダイアログ / 操作ログ / ワンクリック復元
    // が担保している。守っていないものを守っているように見せない。
    // =================================================================

    private void OnSelectionChanged(object sender, RoutedEventArgs e)
    {
        var selected = SelectedRows();

        SelectionText.Text = selected.Count switch
        {
            0 => "タイトルを選択すると操作できます（Ctrl / Shift クリックで複数選択）",
            1 => $"{selected[0].Title} — {selected[0].SizeText}",
            _ => $"{selected.Count} 件選択中 / 合計 {AdviceFormatter.Bytes(selected.Sum(r => r.SizeBytes))}"
                 + $" / 圧縮見込み {AdviceFormatter.Bytes(selected.Sum(r => r.SavedBytes))}",
        };

        UpdateActionButtons();
    }

    private List<ResultRow> SelectedRows() => ResultList.SelectedItems.OfType<ResultRow>().ToList();

    private void UpdateActionButtons()
    {
        var ready = ResultList.SelectedItems.Count > 0 && _operationCts is null;

        CompressButton.IsEnabled = ready;
        RestoreButton.IsEnabled = ready;
    }

    private async void OnCompressClick(object sender, RoutedEventArgs e) => await RunBatchAsync(compress: true);

    private async void OnRestoreClick(object sender, RoutedEventArgs e) => await RunBatchAsync(compress: false);

    private void OnCancelClick(object sender, RoutedEventArgs e) => _operationCts?.Cancel();

    private async Task RunBatchAsync(bool compress)
    {
        var rows = SelectedRows();
        if (rows.Count == 0 || _steamRoot is null) return;

        // 選択順ではなく画面の並び順で処理する（ユーザーの見た目と一致させる）
        rows = rows.OrderBy(r => r.GroupOrder).ThenBy(r => r.Title, StringComparer.CurrentCulture).ToList();

        // スキャン結果は古くなっている可能性があるので、操作直前に manifest から取り直す
        var reader = new SteamReader(_fs);
        var installed = reader.ReadLibraries(_steamRoot)
            .SelectMany(reader.ReadInstalledApps)
            .ToDictionary(a => a.AppId);

        var targets = new List<InstalledApp>();
        var missing = new List<string>();

        foreach (var row in rows)
        {
            if (installed.TryGetValue(row.AppId, out var app)) targets.Add(app);
            else missing.Add(row.Title);
        }

        if (targets.Count == 0)
        {
            MessageBox.Show(this,
                "選択したタイトルのインストールが見つかりませんでした。スキャン後に移動・削除された可能性があります。",
                "実行できません", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // --- 事前検査（fail-closed）を全件に対して先に通す ---
        StatusText.Text = "事前検査中...";
        var preFlight = CreatePreFlightChecker();

        var runnable = new List<InstalledApp>();
        var blocked = new List<string>();
        var warnings = new List<string>();

        foreach (var app in targets)
        {
            var report = await Task.Run(() => preFlight.Check(app));

            if (report.CanProceed)
            {
                runnable.Add(app);
                warnings.AddRange(report.Issues.Where(i => !i.Blocks)
                    .Select(i => $"{app.Name}: {PreFlightFormatter.Describe(i)}"));
            }
            else
            {
                blocked.AddRange(report.Issues.Where(i => i.Blocks)
                    .Select(i => $"{app.Name}: {PreFlightFormatter.Describe(i)}"));
            }
        }

        StatusText.Text = "解析完了";

        if (runnable.Count == 0)
        {
            MessageBox.Show(this,
                "事前検査で問題が見つかったため実行しません。ファイルは変更していません。\n\n"
                + string.Join("\n", blocked),
                "実行できません", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // --- 確認ダイアログ。実行対象と、除外されたものを両方見せる ---
        var text = compress
            ? $"{runnable.Count} 件を圧縮します。\n\n"
              + string.Join("\n", runnable.Select(a =>
                  $"・{a.Name}  {AdviceFormatter.Bytes(SelectedSize(rows, a.AppId))}"
                  + $"（見込み {AdviceFormatter.Bytes(SelectedSaving(rows, a.AppId))}）"))
              + "\n\n圧縮は「元に戻す」でいつでも解除できます。ゲーム更新の書き込みでも自動的に解除されます。"
            : $"{runnable.Count} 件の圧縮を解除して元に戻します。\n\n"
              + string.Join("\n", runnable.Select(a => $"・{a.Name}"));

        if (missing.Count > 0) text += "\n\n見つからないため除外:\n" + string.Join("\n", missing.Select(m => $"・{m}"));
        if (blocked.Count > 0) text += "\n\n事前検査で除外:\n" + string.Join("\n", blocked);
        if (warnings.Count > 0) text += "\n\n注意:\n" + string.Join("\n", warnings);

        var answer = MessageBox.Show(this, text,
            compress ? "圧縮の確認" : "復元の確認",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK) return;

        // --- 逐次実行 ---
        var engine = new CompactExeEngine(_fs);
        var journal = new OperationJournal(OperationJournal.DefaultPath);

        _operationCts = new CancellationTokenSource();
        SetBusy(true);

        var succeeded = 0;
        long totalSaved = 0;
        var failures = new List<string>();

        try
        {
            for (var i = 0; i < runnable.Count; i++)
            {
                if (_operationCts.IsCancellationRequested) break;

                var app = runnable[i];
                var index = i;

                var progress = new Progress<CompressionProgress>(p =>
                    StatusText.Text = $"[{index + 1}/{runnable.Count}] {(compress ? "圧縮中" : "復元中")}: "
                                      + $"{app.Name}  {p.FilesProcessed} ファイル処理");

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
                    failures.Add($"{app.Name}: 操作ログを書き込めませんでした ({ex.Message})");
                }

                if (result.Success)
                {
                    succeeded++;
                    totalSaved += result.BytesSaved;
                }
                else
                {
                    failures.Add($"{app.Name}: {result.ErrorMessage}");
                }
            }

            var verb = compress ? "圧縮" : "復元";
            StatusText.Text =
                $"{verb}完了: {succeeded}/{runnable.Count} 件  "
                + $"{(compress ? "削減" : "復元")} {AdviceFormatter.Bytes(Math.Abs(totalSaved))}"
                + "（表示を最新にするには再スキャンしてください）";

            if (failures.Count > 0)
            {
                MessageBox.Show(this,
                    $"{succeeded}/{runnable.Count} 件が完了しました。\n\n完了しなかったもの:\n"
                    + string.Join("\n", failures)
                    + "\n\n途中まで処理された状態も有効です。「元に戻す」でいつでも解除できます。",
                    "一部が完了しませんでした", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            SetBusy(false);
        }
    }

    private static long SelectedSize(List<ResultRow> rows, long appId)
        => rows.FirstOrDefault(r => r.AppId == appId)?.SizeBytes ?? 0;

    private static long SelectedSaving(List<ResultRow> rows, long appId)
        => rows.FirstOrDefault(r => r.AppId == appId)?.SavedBytes ?? 0;

    private PreFlightChecker CreatePreFlightChecker() => new(
        _fs,
        runningProcessNames: () => System.Diagnostics.Process.GetProcesses()
            .Select(p => { try { return p.ProcessName; } catch { return string.Empty; } })
            .Where(n => n.Length > 0)
            .ToList(),
        isFileInUse: FileLockProbe.IsFileInUse);

    private void SetBusy(bool busy)
    {
        ScanButton.IsEnabled = !busy;
        ResultList.IsEnabled = !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
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
