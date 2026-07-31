using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
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
/// <summary>
/// 一覧の 1 行。
///
/// 圧縮・復元を実行したら、その行を**実測値で**その場更新する。
/// 実行結果には前後の実占有サイズが入っており、これは再スキャンの推定値より正確。
/// 「表示を最新にするには再スキャンしてください」と 4 分の作業を要求するのは、
/// 手元により良い情報があるのに捨てているだけだった。
///
/// 変更通知を出すのは、グループ移動と並べ替えを自動で追従させるため
/// （ListCollectionView の IsLiveGrouping / IsLiveSorting）。
/// </summary>
public sealed class ResultRow : INotifyPropertyChanged
{
    public required long AppId { get; init; }

    public required string Title { get; init; }

    public required long SizeBytes { get; init; }

    public required string PlayedText { get; init; }

    public required int DaysSincePlayedSort { get; init; }

    /// <summary>復元したときに戻す先の分類。</summary>
    public string OriginalGroup { get; set; } = string.Empty;

    public int OriginalGroupOrder { get; set; }

    public long OriginalSavedBytes { get; set; }

    public string OriginalSavingText { get; set; } = "—";

    public string OriginalReasons { get; set; } = string.Empty;

    public string SizeText => AdviceFormatter.Bytes(SizeBytes);

    private int _groupOrder;
    public int GroupOrder { get => _groupOrder; set => Set(ref _groupOrder, value); }

    private string _group = string.Empty;
    public string Group { get => _group; set => Set(ref _group, value); }

    private string _savingText = "—";
    public string SavingText { get => _savingText; set => Set(ref _savingText, value); }

    private long _savedBytes;
    public long SavedBytes { get => _savedBytes; set => Set(ref _savedBytes, value); }

    private string _reasons = string.Empty;
    public string Reasons { get => _reasons; set => Set(ref _reasons, value); }

    /// <summary>この起動中に圧縮・復元を実行したか。選択が外れても何をしたか分かるように残す。</summary>
    private bool _touchedThisSession;
    public bool TouchedThisSession { get => _touchedThisSession; set => Set(ref _touchedThisSession, value); }

    public bool IsCompressed => GroupOrder == CompressedGroupOrder;

    public const int CompressedGroupOrder = 5;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// スクリーンリーダーはこの文字列を読み上げる。
    /// 既定の ToString() はクラス名になって実用にならない。
    /// </summary>
    public override string ToString() => $"{Title} / {SizeText} / {SavingText} / {PlayedText}";
}

public partial class MainWindow : Window
{
    private readonly PhysicalFileSystem _fs = new();
    private string? _steamRoot;
    private CancellationTokenSource? _operationCts;
    private readonly System.Diagnostics.Stopwatch _elapsed = new();

    /// <summary>
    /// 経過・残り時間の表示を 1 秒ごとに更新する。
    ///
    /// 進捗の報告はタイトル単位でしか来ない。100GB 級のタイトルを処理している間は
    /// 十数秒〜数分にわたって報告が届かず、時間表示が固まって「止まった」ように見える。
    /// 待ち時間を短く感じさせるには、数字が動き続けていることが要る。
    /// </summary>
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromSeconds(1) };

    private double _lastFraction;

    private string _sortColumn = string.Empty;
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;
    private List<ResultRow> _rows = [];

    /// <summary>グループの並び順。ソートしてもこの順序は崩さない。</summary>
    private static readonly AdviceKind[] GroupOrderList =
    [
        AdviceKind.Compress,
        AdviceKind.CompressUpdatesOften,
        AdviceKind.CompressAntiCheat,
        AdviceKind.NotWorthCompressing,
        AdviceKind.DoNotCompress,
        AdviceKind.AlreadyCompressed,
    ];

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        _ticker.Tick += (_, _) => ProgressEta.Text = EstimateRemaining(_lastFraction);
    }

    // =================================================================
    // 起動直後: タイトル一覧だけ即座に出す（圧縮見込みの解析はまだ走らせない）
    //
    // 圧縮率の実測は数分かかる。それを待たせて空の画面を見せるのは
    // 「起動したのか分からない」という最悪の第一印象になる。
    // manifest の読み取りだけなら実測 50〜400ms で済むので先に出す（D-016）。
    // =================================================================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _steamRoot ??= new SteamLocator(_fs, LocateSteamFromRegistry).Locate();

            if (_steamRoot is null)
            {
                StatusText.Text = "Steam のインストール先が見つかりませんでした。";
                ScanButton.IsEnabled = false;
                return;
            }

            var titles = new LibraryScanner(_fs).ReadTitles(_steamRoot);
            ShowTitles(titles);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"一覧を読めませんでした: {ex.Message}";
        }
    }

    private void ShowTitles(IReadOnlyList<TitleSummary> titles)
    {
        var rows = titles
            .Select(t =>
            {
                var reasons = t.IsFullyInstalled
                    ? "「圧縮見込みを解析」を押すと、実際のファイルを読んで圧縮率を測ります"
                    : "インストールが完了していません（ダウンロード中・更新中の可能性）";

                return new ResultRow
                {
                    AppId = t.AppId,
                    Title = t.Name,
                    SizeBytes = t.SizeBytes,
                    PlayedText = t.DaysSincePlayed is { } d ? $"{AdviceFormatter.Duration(d)}前" : "記録なし",
                    DaysSincePlayedSort = t.DaysSincePlayed ?? int.MaxValue,
                    GroupOrder = 0,
                    Group = "未解析（サイズの大きい順）",
                    SavingText = "—",
                    SavedBytes = 0,
                    Reasons = reasons,
                    OriginalGroup = "未解析（サイズの大きい順）",
                    OriginalGroupOrder = 0,
                    OriginalSavingText = "—",
                    OriginalReasons = reasons,
                };
            })
            .ToList();

        BindRows(rows);
        StatusText.Text = "一覧を表示しました。圧縮見込みはまだ測っていません。";
    }

    /// <summary>
    /// 行を画面に結びつける。グループ移動と並べ替えを自動追従させる設定をここで入れる。
    /// </summary>
    private void BindRows(List<ResultRow> rows)
    {
        _rows = rows;

        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ResultRow.Group)));

        // 行の内容を書き換えたら、グループも並び順も勝手に追従してほしい。
        // これが無いと、圧縮しても行がその場に残り「本当に実行されたのか」が分からない
        view.IsLiveGrouping = true;
        view.LiveGroupingProperties.Add(nameof(ResultRow.Group));
        view.IsLiveSorting = true;

        ResultList.ItemsSource = view;
        ApplySort();
        UpdateSummary();
    }

    /// <summary>合計は行から導く。圧縮を実行したら即座に反映されるように。</summary>
    private void UpdateSummary()
    {
        if (_rows.Count == 0) return;

        var total = _rows.Sum(r => r.SizeBytes);
        var remaining = _rows.Where(r => !r.IsCompressed).Sum(r => r.SavedBytes);
        var realized = _rows.Where(r => r.IsCompressed).Sum(r => r.SavedBytes);

        var text = $"タイトル {_rows.Count} 件 / 合計 {AdviceFormatter.Bytes(total)}";

        if (realized > 0) text += $" / 圧縮済み {AdviceFormatter.Bytes(realized)} 削減中";
        if (remaining > 0) text += $" / まだ圧縮できる見込み {AdviceFormatter.Bytes(remaining)}";

        SummaryText.Text = text;
    }

    // =================================================================
    // スキャン（読み取り専用）
    // =================================================================

    private async void OnScanClick(object sender, RoutedEventArgs e)
    {
        if (_steamRoot is null)
        {
            StatusText.Text = "Steam のインストール先が見つかりませんでした。";
            return;
        }

        _operationCts = new CancellationTokenSource();
        SetBusy(true);
        BeginProgress("圧縮見込みを解析しています");

        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                if (p.CurrentTitle is null) return;

                UpdateProgress(
                    p.Fraction,
                    $"解析中 [{p.Completed + 1}/{p.Total}] {p.CurrentTitle}");
            });

            // 判定は全て Core 側。UI スレッドを塞がないよう別スレッドで回す
            var scanner = new LibraryScanner(_fs);
            var steamRoot = _steamRoot;
            var token = _operationCts.Token;
            var result = await Task.Run(() => scanner.Scan(steamRoot, progress, token), token);

            ShowResult(result);
            ScanButton.Content = "再解析（数分）";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "解析を中止しました（途中までの結果は表示していません）。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"エラー: {ex.Message}";
        }
        finally
        {
            _operationCts?.Dispose();
            _operationCts = null;
            EndProgress();
            SetBusy(false);
        }
    }

    // =================================================================
    // 進捗表示
    //
    // 進捗率は件数ではなくバイト数で出す（ScanProgress.Fraction）。
    // 「44 件中 40 件終わったのに 30%」は正しい表示で、
    // 残りに 117GB のタイトルが控えているという事実を伝えている。
    // =================================================================

    private void BeginProgress(string caption)
    {
        _elapsed.Restart();
        _lastFraction = 0;
        ProgressArea.Visibility = Visibility.Visible;
        ProgressGauge.IsIndeterminate = false;
        ProgressGauge.Value = 0;
        ProgressPercent.Text = "0%";
        ProgressCaption.Text = caption;
        ProgressEta.Text = "経過 0 秒";
        _ticker.Start();
    }

    private void UpdateProgress(double fraction, string caption)
    {
        _lastFraction = Math.Clamp(fraction, 0.0, 1.0);
        ProgressGauge.IsIndeterminate = false;
        ProgressGauge.Value = _lastFraction;
        ProgressPercent.Text = $"{_lastFraction:P0}";
        ProgressCaption.Text = caption;
        ProgressEta.Text = EstimateRemaining(_lastFraction);
        StatusText.Text = caption;
    }

    /// <summary>総量が読めない処理（compact.exe の進行中など）のための表示。</summary>
    private void UpdateProgressIndeterminate(string caption)
    {
        _lastFraction = 0;
        ProgressGauge.IsIndeterminate = true;
        ProgressPercent.Text = string.Empty;
        ProgressCaption.Text = caption;
        StatusText.Text = caption;
    }

    private void EndProgress()
    {
        _ticker.Stop();
        _elapsed.Stop();
        ProgressArea.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 残り時間の目安。進捗が浅いうちは推定が暴れるので出さない
    /// （「残り 3 時間」と出してから 1 分で終わるのは、無いより悪い）。
    /// </summary>
    private string EstimateRemaining(double fraction)
    {
        if (fraction < 0.05 || fraction >= 1.0) return $"経過 {Duration(_elapsed.Elapsed)}";

        var remaining = TimeSpan.FromSeconds(
            _elapsed.Elapsed.TotalSeconds / fraction * (1 - fraction));

        return $"残り {Duration(remaining)} 前後";
    }

    private static string Duration(TimeSpan span) => span.TotalMinutes >= 1
        ? $"{(int)span.TotalMinutes} 分 {span.Seconds} 秒"
        : $"{span.Seconds} 秒";

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _operationCts?.Cancel();
        StatusText.Text = "中止しています...";
    }

    private void ShowResult(ScanResult result)
    {
        var rows = result.Assessments
            .Select(a =>
            {
                var compressed = a.Advice == AdviceKind.AlreadyCompressed;
                var saved = compressed
                    ? Math.Max(0, a.SizeBytes - a.PhysicalBytes)
                    : a.Estimate.EstimatedSavedBytes;

                var savingText = compressed
                    ? $"圧縮済み {AdviceFormatter.Bytes(saved)}"
                    : AdviceFormatter.Bytes(saved);

                var reasons = string.Join(" / ",
                    a.Reasons
                        .Where(r => r != ReasonCode.RecentlyPlayed)
                        .Select(r => AdviceFormatter.Describe(r, a)));

                var order = Array.IndexOf(GroupOrderList, a.Advice);

                return new ResultRow
                {
                    AppId = a.AppId,
                    Title = a.Name,
                    SizeBytes = a.SizeBytes,
                    PlayedText = a.DaysSincePlayed is { } d ? $"{AdviceFormatter.Duration(d)}前" : "記録なし",
                    // 起動記録なしは「最も古い」側に置く（未起動を上位に集めたい用途に合う）
                    DaysSincePlayedSort = a.DaysSincePlayed ?? int.MaxValue,
                    GroupOrder = order,
                    Group = AdviceFormatter.Label(a.Advice),
                    SavingText = savingText,
                    SavedBytes = saved,
                    Reasons = reasons,
                    OriginalGroup = AdviceFormatter.Label(a.Advice),
                    OriginalGroupOrder = order,
                    OriginalSavedBytes = a.Estimate.EstimatedSavedBytes,
                    OriginalSavingText = AdviceFormatter.Bytes(a.Estimate.EstimatedSavedBytes),
                    OriginalReasons = reasons,
                };
            })
            .ToList();

        BindRows(rows);

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
            view.LiveSortingProperties.Clear();

            // 第1キーは常にグループ順。これを外すと並べ替えでグループの並びまで変わる
            view.SortDescriptions.Add(
                new SortDescription(nameof(ResultRow.GroupOrder), ListSortDirection.Ascending));
            view.LiveSortingProperties.Add(nameof(ResultRow.GroupOrder));

            if (_sortColumn.Length > 0)
            {
                view.SortDescriptions.Add(new SortDescription(_sortColumn, _sortDirection));
                view.LiveSortingProperties.Add(_sortColumn);
            }
            else
            {
                // 既定は削減量の大きい順。圧縮した行が「圧縮済み」の先頭に来る
                view.SortDescriptions.Add(
                    new SortDescription(nameof(ResultRow.SavedBytes), ListSortDirection.Descending));
                view.LiveSortingProperties.Add(nameof(ResultRow.SavedBytes));
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

        var analyzed = selected.Count > 0 && selected.All(r => r.SavingText != "—");

        SelectionText.Text = selected.Count switch
        {
            0 => "タイトルを選択すると操作できます（Ctrl / Shift クリックで複数選択）",
            1 => $"{selected[0].Title} — {selected[0].SizeText}"
                 + (analyzed ? $" / 圧縮見込み {selected[0].SavingText}" : " / 圧縮見込みは未計測"),
            _ => $"{selected.Count} 件選択中 / 合計 {AdviceFormatter.Bytes(selected.Sum(r => r.SizeBytes))}"
                 + (analyzed
                     ? $" / 圧縮見込み {AdviceFormatter.Bytes(selected.Sum(r => r.SavedBytes))}"
                     : " / 圧縮見込みは未計測"),
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
                  // 未解析のまま実行することもできる。その場合に見込み 0 B と出すと
                  // 「圧縮しても無駄」という誤った印象を与えるので、測っていないと明示する
                  + (SelectedAnalyzed(rows, a.AppId)
                      ? $"（見込み {AdviceFormatter.Bytes(SelectedSaving(rows, a.AppId))}）"
                      : "（圧縮見込みは未計測）")))
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
        BeginProgress(compress ? "圧縮しています" : "元に戻しています");

        var succeeded = 0;
        long totalSaved = 0;
        var failures = new List<string>();

        // 圧縮も所要時間は容量に比例する。件数ではなくバイト数で進捗を出す
        var plannedBytes = runnable.Sum(a => SelectedSize(rows, a.AppId));
        long doneBytes = 0;

        try
        {
            for (var i = 0; i < runnable.Count; i++)
            {
                if (_operationCts.IsCancellationRequested) break;

                var app = runnable[i];
                var index = i;
                var appBytes = SelectedSize(rows, app.AppId);
                var baseBytes = doneBytes;

                var progress = new Progress<CompressionProgress>(p =>
                {
                    var caption = $"[{index + 1}/{runnable.Count}] {(compress ? "圧縮中" : "復元中")}: "
                                  + $"{app.Name}  {p.FilesProcessed} ファイル処理";

                    if (plannedBytes > 0)
                    {
                        // 1 タイトル内の進行度は分からないので、
                        // 完了済みバイト数だけで測る（タイトルが終わるたびに進む）
                        UpdateProgress((double)baseBytes / plannedBytes, caption);
                    }
                    else
                    {
                        UpdateProgressIndeterminate(caption);
                    }
                });

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

                    // 実測の前後サイズで行を更新する。再スキャンより正確で、しかも即座
                    ApplyResultToRow(app.AppId, result, compress);
                }
                else
                {
                    failures.Add($"{app.Name}: {result.ErrorMessage}");
                }

                doneBytes += appBytes;
                if (plannedBytes > 0)
                {
                    UpdateProgress((double)doneBytes / plannedBytes,
                        $"[{i + 1}/{runnable.Count}] 完了: {app.Name}");
                }
            }

            var verb = compress ? "圧縮" : "復元";
            StatusText.Text =
                $"{verb}完了: {succeeded}/{runnable.Count} 件  "
                + $"{(compress ? "削減" : "復元")} {AdviceFormatter.Bytes(Math.Abs(totalSaved))}"
                + "（一覧に反映済み）";

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
            EndProgress();
            SetBusy(false);
        }
    }

    /// <summary>
    /// 圧縮・復元の実測結果を行に反映する。
    ///
    /// 行の分類・削減量・根拠を書き換えると、グループ移動と並べ替えが自動で追従する。
    /// 圧縮したタイトルが目の前で「圧縮済み」へ移動するので、
    /// 実行されたかどうかを再スキャンで確かめる必要がない。
    /// </summary>
    private void ApplyResultToRow(long appId, CompressionResult result, bool compress)
    {
        var row = _rows.FirstOrDefault(r => r.AppId == appId);
        if (row is null) return;

        row.TouchedThisSession = true;

        if (compress)
        {
            var saved = Math.Max(0, result.BytesSaved);

            row.GroupOrder = ResultRow.CompressedGroupOrder;
            row.Group = AdviceFormatter.Label(AdviceKind.AlreadyCompressed);
            row.SavedBytes = saved;
            row.SavingText = $"圧縮済み {AdviceFormatter.Bytes(saved)}";
            row.Reasons = $"この操作で圧縮しました: {AdviceFormatter.Bytes(result.BytesBefore)}"
                          + $" → {AdviceFormatter.Bytes(result.BytesAfter)}"
                          + $"（{AdviceFormatter.Bytes(saved)} 削減・実測値）";
        }
        else
        {
            // 解除したら元の分類に戻す。見込みは解析時の推定値に戻る
            row.GroupOrder = row.OriginalGroupOrder;
            row.Group = row.OriginalGroup;
            row.SavedBytes = row.OriginalSavedBytes;
            row.SavingText = row.OriginalSavingText;
            row.Reasons = $"この操作で圧縮を解除しました: {AdviceFormatter.Bytes(result.BytesBefore)}"
                          + $" → {AdviceFormatter.Bytes(result.BytesAfter)}（実測値）";
        }

        UpdateSummary();
    }

    private static long SelectedSize(List<ResultRow> rows, long appId)
        => rows.FirstOrDefault(r => r.AppId == appId)?.SizeBytes ?? 0;

    private static long SelectedSaving(List<ResultRow> rows, long appId)
        => rows.FirstOrDefault(r => r.AppId == appId)?.SavedBytes ?? 0;

    private static bool SelectedAnalyzed(List<ResultRow> rows, long appId)
        => rows.FirstOrDefault(r => r.AppId == appId)?.SavingText is { } s && s != "—";

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
        // 中止手段は 1 つに統一する（解析・圧縮とも上の「中止」ボタン）
        StopButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
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
