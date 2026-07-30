namespace SteamChecker.Core.Compression;

/// <summary>
/// ファイルが他プロセスに使用中かを排他オープンの可否で判定する。
/// 実行中の exe はイメージとして掴まれているため排他オープンが失敗する。
/// CLI / WPF の両方から <see cref="PreFlightChecker"/> に注入して使う。
/// </summary>
public static class FileLockProbe
{
    /// <summary>判定できない場合は「使用中」を返す（fail-closed）。</summary>
    public static bool IsFileInUse(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
