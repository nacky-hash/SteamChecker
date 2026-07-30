using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SteamChecker.Core;

/// <summary>実ファイルシステム実装。</summary>
public sealed class PhysicalFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public Stream? OpenRead(string path)
    {
        try
        {
            // 実行中のゲームがファイルを掴んでいても読めるよう共有指定を緩める
            return new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern)
    {
        if (!Directory.Exists(directory)) return [];

        try
        {
            return Directory.EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// 再帰列挙。アクセス拒否のフォルダで例外を投げず、そこだけ飛ばして続行する。
    /// （Directory.EnumerateFiles の再帰版は途中で例外を投げて全体が止まるため自前で回す）
    /// </summary>
    public IEnumerable<string> EnumerateFilesRecursive(string directory)
    {
        var stack = new Stack<string>();
        stack.Push(directory);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var f in files) yield return f;

            string[] subdirs;
            try
            {
                subdirs = Directory.GetDirectories(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var d in subdirs)
            {
                // リパースポイント（ジャンクション/シンボリックリンク）は辿らない。無限ループ防止
                try
                {
                    if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                stack.Push(d);
            }
        }
    }

    public IEnumerable<string> EnumerateDirectories(string directory)
    {
        if (!Directory.Exists(directory)) return [];

        try
        {
            return Directory.EnumerateDirectories(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public long GetFileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public long GetFileSizeOnDisk(string path)
    {
        if (!OperatingSystem.IsWindows()) return GetFileSize(path);

        return GetCompressedSizeWindows(path);
    }

    [SupportedOSPlatform("windows")]
    private static long GetCompressedSizeWindows(string path)
    {
        var low = GetCompressedFileSizeW(path, out var high);
        if (low == 0xFFFFFFFF && Marshal.GetLastWin32Error() != 0)
        {
            try { return new FileInfo(path).Length; }
            catch { return 0; }
        }

        return ((long)high << 32) | low;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);

    public DateTimeOffset GetLastWriteTimeUtc(string path)
    {
        try
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    public string Combine(params string[] parts) => Path.Combine(parts);

    public string GetFileName(string path) => Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, '/'));

    public string GetExtension(string path) => Path.GetExtension(path);

    public string? GetVolumeRoot(string path)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public string? GetFileSystemName(string path)
    {
        var root = GetVolumeRoot(path);
        if (string.IsNullOrEmpty(root)) return null;

        try
        {
            return new DriveInfo(root).DriveFormat;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 判定できないものを「安全」と扱わない
            return true;
        }
    }

    public long? GetAvailableFreeBytes(string path)
    {
        var root = GetVolumeRoot(path);
        if (string.IsNullOrEmpty(root)) return null;

        try
        {
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
