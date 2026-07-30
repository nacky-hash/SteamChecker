using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;
using SteamChecker.Core;
using SteamChecker.Core.Analysis;
using SteamChecker.Core.Presentation;
using SteamChecker.Core.Steam;

namespace SteamChecker.App;

/// <summary>
/// 一覧表示のための行データ。表示用文字列だけを持つ。
/// 判定は Core（LibraryScanner / Advisor）が済ませたものをそのまま並べる。
/// </summary>
public sealed record ResultRow(string Group, string Title, string Summary, string Reasons);

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnScanClick(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        ResultList.ItemsSource = null;
        SummaryText.Text = string.Empty;

        try
        {
            var fs = new PhysicalFileSystem();
            var steamRoot = new SteamLocator(fs, LocateSteamFromRegistry).Locate();

            if (steamRoot is null)
            {
                StatusText.Text = "Steam のインストール先が見つかりませんでした。";
                return;
            }

            var progress = new Progress<ScanProgress>(p =>
                StatusText.Text = p.CurrentTitle is null
                    ? $"解析完了 ({p.Completed}/{p.Total})"
                    : $"解析中 [{p.Completed + 1}/{p.Total}] {p.CurrentTitle}");

            // 判定は全て Core 側。UI スレッドを塞がないよう別スレッドで回す
            var scanner = new LibraryScanner(fs);
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
