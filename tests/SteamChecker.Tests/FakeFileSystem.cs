using SteamChecker.Core;

namespace SteamChecker.Tests;

/// <summary>
/// テスト用のインメモリファイルシステム。Windows 形式のパス（C:\... 区切りは \）を
/// Linux 上でそのまま再現するために自前で実装している。
/// Path.Combine を使うと Linux では '/' で連結されてしまい、
/// 実環境と違う経路をテストすることになる。
/// </summary>
public sealed class FakeFileSystem : IFileSystem
{
    private readonly Dictionary<string, FakeFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

    private sealed record FakeFile(byte[] Content, long SizeOnDisk, DateTimeOffset LastWrite)
    {
        public long Length => Content.LongLength;
    }

    /// <summary>ボリュームルート → ファイルシステム名。</summary>
    public Dictionary<string, string> VolumeFileSystems { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"C:\"] = "NTFS",
    };

    /// <summary>ボリュームルート → 空き容量（バイト）。未設定なら十分な空きがあるとみなす。</summary>
    public Dictionary<string, long> VolumeFreeBytes { get; } = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _reparsePoints = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>ジャンクション / シンボリックリンクを模擬する。</summary>
    public FakeFileSystem MarkReparsePoint(string path)
    {
        _reparsePoints.Add(Normalize(path));
        return this;
    }

    // ---------------------------------------------------------------
    // セットアップ用ヘルパー
    // ---------------------------------------------------------------

    /// <summary>
    /// ファイルを追加する。
    /// <paramref name="logicalSize"/> を指定すると「サイズは巨大だが中身のサンプルは小さい」
    /// ファイルを作れる。実環境で 20GB の exe から 4MB だけサンプリングする状況を
    /// テストで再現するために使う。
    /// </summary>
    public FakeFileSystem AddFile(
        string path,
        byte[] content,
        long? sizeOnDisk = null,
        DateTimeOffset? lastWrite = null,
        long? logicalSize = null)
    {
        var normalized = Normalize(path);
        _files[normalized] = new FakeFile(
            content,
            sizeOnDisk ?? logicalSize ?? content.LongLength,
            lastWrite ?? DateTimeOffset.UnixEpoch);

        if (logicalSize is { } size) _sizeOverrides[normalized] = size;

        AddParentDirectories(normalized);
        return this;
    }

    public FakeFileSystem AddTextFile(string path, string content, DateTimeOffset? lastWrite = null)
        => AddFile(path, System.Text.Encoding.UTF8.GetBytes(content), lastWrite: lastWrite);

    /// <summary>実体のない「サイズだけあるファイル」を作る。巨大フォルダの再現用。</summary>
    public FakeFileSystem AddSizedFile(string path, long logicalSize, long? sizeOnDisk = null)
    {
        var normalized = Normalize(path);
        _files[normalized] = new FakeFile([], sizeOnDisk ?? logicalSize, DateTimeOffset.UnixEpoch);
        _sizeOverrides[normalized] = logicalSize;
        AddParentDirectories(normalized);
        return this;
    }

    private readonly Dictionary<string, long> _sizeOverrides = new(StringComparer.OrdinalIgnoreCase);

    public FakeFileSystem AddDirectory(string path)
    {
        var normalized = Normalize(path);
        _directories.Add(normalized);
        AddParentDirectories(normalized + @"\x");
        return this;
    }

    private void AddParentDirectories(string filePath)
    {
        var idx = filePath.LastIndexOf('\\');
        while (idx > 0)
        {
            var dir = filePath[..idx];
            if (!_directories.Add(dir)) break;
            idx = dir.LastIndexOf('\\');
        }
    }

    private static string Normalize(string path)
        => path.Replace('/', '\\').TrimEnd('\\');

    // ---------------------------------------------------------------
    // IFileSystem
    // ---------------------------------------------------------------

    public bool FileExists(string path) => _files.ContainsKey(Normalize(path));

    public bool DirectoryExists(string path)
    {
        var n = Normalize(path);
        return _directories.Contains(n) || _files.Keys.Any(f => f.StartsWith(n + @"\", StringComparison.OrdinalIgnoreCase));
    }

    public string ReadAllText(string path)
    {
        var n = Normalize(path);
        if (!_files.TryGetValue(n, out var file)) throw new FileNotFoundException(path);
        return System.Text.Encoding.UTF8.GetString(file.Content);
    }

    public Stream? OpenRead(string path)
    {
        var n = Normalize(path);
        return _files.TryGetValue(n, out var file) ? new MemoryStream(file.Content, writable: false) : null;
    }

    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern)
    {
        var prefix = Normalize(directory) + @"\";
        var regex = PatternToRegex(searchPattern);

        return _files.Keys
            .Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Where(f => !f[prefix.Length..].Contains('\\'))
            .Where(f => regex.IsMatch(f[prefix.Length..]))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static System.Text.RegularExpressions.Regex PatternToRegex(string pattern)
    {
        var escaped = System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".");

        return new System.Text.RegularExpressions.Regex(
            "^" + escaped + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    public IEnumerable<string> EnumerateFilesRecursive(string directory)
    {
        var prefix = Normalize(directory) + @"\";
        return _files.Keys
            .Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IEnumerable<string> EnumerateDirectories(string directory)
    {
        var prefix = Normalize(directory) + @"\";
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in _files.Keys.Concat(_directories))
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var rest = key[prefix.Length..];
            var slash = rest.IndexOf('\\');
            if (slash > 0) result.Add(prefix + rest[..slash]);
        }

        return result.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public long GetFileSize(string path)
    {
        var n = Normalize(path);
        if (_sizeOverrides.TryGetValue(n, out var overridden)) return overridden;
        return _files.TryGetValue(n, out var file) ? file.Length : 0;
    }

    public long GetFileSizeOnDisk(string path)
        => _files.TryGetValue(Normalize(path), out var file) ? file.SizeOnDisk : 0;

    public DateTimeOffset GetLastWriteTimeUtc(string path)
        => _files.TryGetValue(Normalize(path), out var file) ? file.LastWrite : DateTimeOffset.MinValue;

    public string Combine(params string[] parts)
        => string.Join('\\', parts.Where(p => !string.IsNullOrEmpty(p)).Select(p => p.TrimEnd('\\')));

    public string GetFileName(string path)
    {
        var n = Normalize(path);
        var idx = n.LastIndexOf('\\');
        return idx >= 0 ? n[(idx + 1)..] : n;
    }

    public string GetExtension(string path)
    {
        var name = GetFileName(path);
        var idx = name.LastIndexOf('.');
        return idx > 0 ? name[idx..] : string.Empty;
    }

    public string? GetVolumeRoot(string path)
    {
        var n = Normalize(path);
        return n.Length >= 2 && n[1] == ':' ? n[..2] + @"\" : null;
    }

    public string? GetFileSystemName(string path)
    {
        var root = GetVolumeRoot(path);
        return root is not null && VolumeFileSystems.TryGetValue(root, out var name) ? name : null;
    }

    public bool IsReparsePoint(string path) => _reparsePoints.Contains(Normalize(path));

    public long? GetAvailableFreeBytes(string path)
    {
        var root = GetVolumeRoot(path);
        if (root is null) return null;

        return VolumeFreeBytes.TryGetValue(root, out var free) ? free : long.MaxValue;
    }
}
