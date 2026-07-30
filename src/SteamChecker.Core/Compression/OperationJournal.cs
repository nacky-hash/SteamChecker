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
