using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamChecker.Core.Compression;

/// <summary>1 操作の記録。</summary>
public sealed record JournalEntry
{
    public required DateTimeOffset Timestamp { get; init; }

    public required string Operation { get; init; }

    public required long AppId { get; init; }

    public required string Name { get; init; }

    public required string Path { get; init; }

    public string? Algorithm { get; init; }

    public required long BytesBefore { get; init; }

    public required long BytesAfter { get; init; }

    public required bool Success { get; init; }

    public string? ErrorMessage { get; init; }

    public double DurationSeconds { get; init; }
}

/// <summary>
/// 実行した操作を追記専用で記録する。
///
/// 無料配布ツールで「圧縮したらゲームが壊れた」という報告は必ず来る。
/// そのとき「いつ・何に・何をしたか」を提示できるかどうかが、
/// 濡れ衣を晴らせるか炎上するかの分かれ目になる。
/// ログはユーザーのためであると同時に、開発者の防御でもある。
///
/// JSON Lines 形式にしているのは、途中でプロセスが落ちても
/// それまでの行が壊れずに残るため。
/// </summary>
public sealed class OperationJournal(string filePath)
{
    private readonly string _filePath = filePath;
    private readonly Lock _gate = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SteamChecker",
        "operations.jsonl");

    public string FilePath => _filePath;

    public void Append(JournalEntry entry)
    {
        var line = JsonSerializer.Serialize(entry, JsonOptions);

        lock (_gate)
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.AppendAllText(_filePath, line + Environment.NewLine, new UTF8Encoding(false));
        }
    }

    public IReadOnlyList<JournalEntry> ReadAll()
    {
        if (!File.Exists(_filePath)) return [];

        var entries = new List<JournalEntry>();

        foreach (var line in File.ReadLines(_filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var entry = JsonSerializer.Deserialize<JournalEntry>(line, JsonOptions);
                if (entry is not null) entries.Add(entry);
            }
            catch (JsonException)
            {
                // 壊れた行は飛ばす。ログのために本体が止まるのが最悪
            }
        }

        return entries;
    }

    /// <summary>
    /// 現在「圧縮済み」として記録されているパスを返す。
    /// 一括復元（全部元に戻す）の対象を決めるのに使う。
    /// </summary>
    public IReadOnlyList<string> CompressedPaths()
    {
        var state = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in ReadAll())
        {
            if (!entry.Success) continue;

            state[entry.Path] = entry.Operation.Equals("compress", StringComparison.OrdinalIgnoreCase);
        }

        return state.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
    }

    /// <summary>
    /// 開始レコードの Operation につける接尾辞。
    /// </summary>
    public const string BeginSuffix = "-begin";

    /// <summary>
    /// 操作の開始を記録する。
    ///
    /// OS にプロセスを強制終了されると完了レコードは書かれない。
    /// 「開始だけが残っている」＝中断された、と次回起動時に判定できるようにする。
    /// メモリ不足で落とされたユーザーが、何が起きたか分からないまま
    /// 放置されるのを防ぐのが目的（D-020）。
    /// </summary>
    public JournalEntry RecordBegin(
        string operation,
        long appId,
        string name,
        string path,
        long bytesBefore,
        CompressionAlgorithm? algorithm = null)
    {
        var entry = new JournalEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Operation = operation + BeginSuffix,
            AppId = appId,
            Name = name,
            Path = path,
            Algorithm = algorithm?.ToCompactArgument(),
            BytesBefore = bytesBefore,
            BytesAfter = bytesBefore,
            Success = false,
            DurationSeconds = 0,
        };

        Append(entry);
        return entry;
    }

    /// <summary>
    /// 開始だけ記録されて完了していない操作を返す（＝途中で落とされた操作）。
    ///
    /// パスごとに最後のエントリを見て、それが開始レコードなら未完了とみなす。
    /// 同じパスを再実行して完了すれば通常の完了レコードで上書きされるため、
    /// 一度報告したものが残り続けることはない。
    /// </summary>
    public IReadOnlyList<JournalEntry> UnfinishedOperations()
    {
        var last = new Dictionary<string, JournalEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in ReadAll())
        {
            last[entry.Path] = entry;
        }

        return last.Values
            .Where(e => e.Operation.EndsWith(BeginSuffix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Timestamp)
            .ToList();
    }

    /// <summary>
    /// パスごとの「過去に圧縮しきったときの実占有バイト数」を返す。
    ///
    /// 圧縮の到達点は決定的で、同じ内容なら何度実行しても同じ値になる
    /// （2026-09-12 に 2 タイトルで各 2 回、完全に一致することを実測）。
    /// サンプリング推定が「まだ縮む」と言っても、実際にそこで止まった実績が
    /// あるなら、そちらが確かな下限になる（D-021）。
    ///
    /// 同じパスに複数回の記録があれば最小値を採る。一度でもそこまで
    /// 縮んだのなら、その値には到達できるため。
    /// </summary>
    public IReadOnlyDictionary<string, long> CompressedFloors()
    {
        var floors = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in ReadAll())
        {
            if (!entry.Success) continue;
            if (!entry.Operation.Equals("compress", StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.BytesAfter <= 0) continue;

            floors[entry.Path] = floors.TryGetValue(entry.Path, out var known)
                ? Math.Min(known, entry.BytesAfter)
                : entry.BytesAfter;
        }

        return floors;
    }

    public JournalEntry Record(
        string operation,
        long appId,
        string name,
        CompressionResult result,
        CompressionAlgorithm? algorithm = null)
    {
        var entry = new JournalEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Operation = operation,
            AppId = appId,
            Name = name,
            Path = result.Path,
            Algorithm = algorithm?.ToCompactArgument(),
            BytesBefore = result.BytesBefore,
            BytesAfter = result.BytesAfter,
            Success = result.Success,
            ErrorMessage = result.ErrorMessage,
            DurationSeconds = result.Duration.TotalSeconds,
        };

        Append(entry);
        return entry;
    }
}
