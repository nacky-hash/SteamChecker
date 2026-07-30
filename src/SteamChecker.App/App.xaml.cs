using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace SteamChecker.App;

public partial class App : Application
{
    /// <summary>
    /// 未処理例外の記録先。無署名の配布物では「起動しない」という報告だけが届きがちで、
    /// 手元で再現できないと何も直せない。せめて原因が残るようにする。
    /// </summary>
    public static string CrashLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SteamChecker",
        "crash.log");

    /// <summary>
    /// 既に通知した例外の署名。描画のたびに同じ例外が出る種類の不具合で
    /// ダイアログを量産しないための歯止め（2026-07-31 に実際に量産した）。
    /// </summary>
    private static readonly HashSet<string> Reported = [];

    private static readonly Lock ReportGate = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            // 続行を試みる（記録が残らないまま消えるのが最悪）。
            // ただし通知は同じ例外につき 1 回だけ
            Record(args.Exception, "UI スレッド");
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Record(ex, "バックグラウンド");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Record(args.Exception, "非同期処理");
            args.SetObserved();
        };
    }

    private static void Record(Exception ex, string where)
    {
        // 同種の例外は 1 回だけ通知する。
        // 描画のたびに投げられる不具合だと、通知するたびに再描画が走って
        // 例外→ダイアログ→再描画→例外… とダイアログが無限に増える
        var signature = $"{ex.GetType().FullName}|{ex.Message}";
        bool first;

        lock (ReportGate)
        {
            first = Reported.Add(signature);
        }

        var text = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {where}"
                   + (first ? string.Empty : "（再発・通知は省略）")
                   + $"\n{ex}\n\n";

        try
        {
            var dir = Path.GetDirectoryName(CrashLogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(CrashLogPath, text, new UTF8Encoding(false));
        }
        catch (Exception)
        {
            // 記録に失敗しても、下の通知だけは出す
        }

        if (!first) return;

        MessageBox.Show(
            $"エラーが発生しました。\n\n{ex.Message}\n\n"
            + $"同じエラーが続く場合は、この画面を閉じてアプリを終了してください。\n"
            + $"詳細を記録しました:\n{CrashLogPath}",
            "SteamChecker", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
