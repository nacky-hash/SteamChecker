namespace SteamChecker.Core;

/// <summary>
/// ファイルシステム抽象。テストで Windows のパス形式を Linux 上で再現するために必要。
/// 実運用では <see cref="PhysicalFileSystem"/> を使う。
/// </summary>
public interface IFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    string ReadAllText(string path);

    /// <summary>
    /// 読み取り専用でファイルを開く。開けなければ null。
    /// 実行中のゲームが掴んでいるファイルも読めるよう、共有指定は緩めること。
    /// </summary>
    Stream? OpenRead(string path);

    /// <summary>指定ディレクトリ直下のファイルを列挙する（再帰しない）。</summary>
    IEnumerable<string> EnumerateFiles(string directory, string searchPattern);

    /// <summary>指定ディレクトリ配下のファイルを再帰的に列挙する。</summary>
    IEnumerable<string> EnumerateFilesRecursive(string directory);

    IEnumerable<string> EnumerateDirectories(string directory);

    long GetFileSize(string path);

    /// <summary>ディスク上の実占有サイズ（圧縮後サイズ）。取得できなければ論理サイズを返す。</summary>
    long GetFileSizeOnDisk(string path);

    DateTimeOffset GetLastWriteTimeUtc(string path);

    string Combine(params string[] parts);

    string GetFileName(string path);

    string GetExtension(string path);

    /// <summary>パスが載っているボリュームのルート（"C:\" など）。判定できなければ null。</summary>
    string? GetVolumeRoot(string path);

    /// <summary>ボリュームのファイルシステム名（"NTFS" / "ReFS" 等）。判定できなければ null。</summary>
    string? GetFileSystemName(string path);

    /// <summary>
    /// パスが reparse point（ジャンクション / シンボリックリンク等）か。
    /// 判定できない場合は true を返すこと（書き込み系の検査は安全側に倒す）。
    /// WOF 圧縮済みファイルには ReparsePoint 属性は立たない（docs/RESEARCH.md §4）ため、
    /// この判定を「圧縮済みか」の判定に流用してはならない（D-005）。
    /// </summary>
    bool IsReparsePoint(string path);

    /// <summary>ボリュームの空き容量（バイト）。判定できなければ null。</summary>
    long? GetAvailableFreeBytes(string path);
}
