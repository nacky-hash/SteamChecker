using SteamChecker.Core.Compression;

namespace SteamChecker.Tests;

/// <summary>
/// 中断の検出（D-020）。
///
/// メモリ不足などで OS にプロセスごと落とされると、完了レコードは書かれない。
/// 「開始だけが残っている」状態を次回起動時に拾えることを固定する。
/// これが無いと、ユーザーは何が起きたか分からないまま放置される。
/// </summary>
public class OperationJournalTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "steamchecker-journal-" + Guid.NewGuid().ToString("N") + ".jsonl");

    public void Dispose()
    {
        try { File.Delete(_path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static CompressionResult Completed(string path) => new()
    {
        Success = true,
        Path = path,
        BytesBefore = 1000,
        BytesAfter = 400,
        FilesProcessed = 3,
        Duration = TimeSpan.FromSeconds(1),
    };

    [Fact]
    public void 開始だけ記録されていれば未完了として返る()
    {
        var journal = new OperationJournal(_path);
        journal.RecordBegin("compress", 1, "Game A", @"C:\games\a", 1000);

        var unfinished = journal.UnfinishedOperations();

        Assert.Single(unfinished);
        Assert.Equal("Game A", unfinished[0].Name);
    }

    [Fact]
    public void 完了まで書かれていれば未完了に含まれない()
    {
        var journal = new OperationJournal(_path);
        journal.RecordBegin("compress", 1, "Game A", @"C:\games\a", 1000);
        journal.Record("compress", 1, "Game A", Completed(@"C:\games\a"));

        Assert.Empty(journal.UnfinishedOperations());
    }

    [Fact]
    public void 中断後に再実行して完了すれば未完了は解消される()
    {
        var journal = new OperationJournal(_path);

        // 1 回目: 開始したところで落とされた
        journal.RecordBegin("compress", 1, "Game A", @"C:\games\a", 1000);

        // 2 回目: 続きから実行して完了した
        journal.RecordBegin("compress", 1, "Game A", @"C:\games\a", 1000);
        journal.Record("compress", 1, "Game A", Completed(@"C:\games\a"));

        Assert.Empty(journal.UnfinishedOperations());
    }

    [Fact]
    public void 別タイトルの完了は未完了判定に影響しない()
    {
        var journal = new OperationJournal(_path);
        journal.RecordBegin("compress", 1, "Game A", @"C:\games\a", 1000);
        journal.Record("compress", 2, "Game B", Completed(@"C:\games\b"));

        var unfinished = journal.UnfinishedOperations();

        Assert.Single(unfinished);
        Assert.Equal(@"C:\games\a", unfinished[0].Path);
    }

    [Fact]
    public void 開始レコードを圧縮済みパスに数えない()
    {
        var journal = new OperationJournal(_path);
        journal.RecordBegin("compress", 1, "Game A", @"C:\games\a", 1000);

        // 開始しただけのものを「圧縮済み」として扱うと、
        // 一括復元の対象に未圧縮のフォルダが混ざる
        Assert.Empty(journal.CompressedPaths());
    }

    [Fact]
    public void 復元の中断も検出できる()
    {
        var journal = new OperationJournal(_path);
        journal.RecordBegin("decompress", 1, "Game A", @"C:\games\a", 400);

        var unfinished = journal.UnfinishedOperations();

        Assert.Single(unfinished);
        Assert.Equal("decompress" + OperationJournal.BeginSuffix, unfinished[0].Operation);
    }
}
