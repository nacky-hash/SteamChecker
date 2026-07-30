using SteamChecker.Core;
using SteamChecker.Core.Analysis;

namespace SteamChecker.Tests;

public class LibraryScannerTests
{
    private const string SteamRoot = @"C:\Program Files (x86)\Steam";

    private static readonly DateTimeOffset Now = new(2026, 7, 28, 0, 0, 0, TimeSpan.Zero);

    private const long GiB = 1024L * 1024 * 1024;

    /// <summary>
    /// 4 タイトルの合成ライブラリ。
    ///   A: よく縮む × 更新も落ち着いている          → 圧縮推奨
    ///   B: 動画だらけ × 3年未起動 × 大容量          → 削除候補
    ///   C: DirectStorage 対応                       → 圧縮非推奨
    ///   D: よく縮むが直近に更新された               → 自動再圧縮つき圧縮
    /// </summary>
    private static FakeFileSystem BuildLibrary()
    {
        var fs = new FakeFileSystem();

        fs.AddTextFile($@"{SteamRoot}\steamapps\libraryfolders.vdf", """
            "libraryfolders"
            {
                "0" { "path" "D:\\SteamLibrary" }
            }
            """);

        AddApp(fs, 100, "Archive Game", 40 * GiB, updatedDaysAgo: 300);
        AddApp(fs, 200, "Movie Game", 60 * GiB, updatedDaysAgo: 500);
        AddApp(fs, 300, "Fast Loader", 30 * GiB, updatedDaysAgo: 400);
        AddApp(fs, 400, "Live Service", 40 * GiB, updatedDaysAgo: 3);

        // A: 圧縮しやすい構成（実行ファイル + テキスト）計 40GiB
        AddCompressibleFile(fs, @"D:\SteamLibrary\steamapps\common\Archive Game\game.exe", 20 * GiB);
        AddCompressibleFile(fs, @"D:\SteamLibrary\steamapps\common\Archive Game\data.json", 20 * GiB);

        // B: 動画だらけ（縮まない）計 60GiB
        AddIncompressibleFile(fs, @"D:\SteamLibrary\steamapps\common\Movie Game\movies\a.mp4", 30 * GiB);
        AddIncompressibleFile(fs, @"D:\SteamLibrary\steamapps\common\Movie Game\movies\b.mp4", 30 * GiB);

        // C: DirectStorage 対応 計 30GiB
        AddCompressibleFile(fs, @"D:\SteamLibrary\steamapps\common\Fast Loader\game.exe", 30 * GiB);
        fs.AddFile(@"D:\SteamLibrary\steamapps\common\Fast Loader\dstorage.dll", new byte[100_000]);

        // D: 圧縮しやすいが更新が活発 計 40GiB
        AddCompressibleFile(fs, @"D:\SteamLibrary\steamapps\common\Live Service\game.exe", 20 * GiB);
        AddCompressibleFile(fs, @"D:\SteamLibrary\steamapps\common\Live Service\config.xml", 20 * GiB);

        fs.AddTextFile($@"{SteamRoot}\userdata\42\config\localconfig.vdf", $$"""
            "UserLocalConfigStore"
            {
                "friends" { "PersonaName" "tester" }
                "Software" { "Valve" { "Steam" { "apps"
                {
                    "100" { "LastPlayed" "{{Unix(20)}}" }
                    "200" { "LastPlayed" "{{Unix(1100)}}" }
                    "300" { "LastPlayed" "{{Unix(5)}}" }
                    "400" { "LastPlayed" "{{Unix(1)}}" }
                } } } }
            }
            """);

        return fs;
    }

    private static long Unix(int daysAgo) => Now.AddDays(-daysAgo).ToUnixTimeSeconds();

    private static void AddApp(
        FakeFileSystem fs, long appId, string name, long sizeOnDisk, int updatedDaysAgo)
    {
        fs.AddTextFile($@"D:\SteamLibrary\steamapps\appmanifest_{appId}.acf", $$"""
            "AppState"
            {
                "appid"       "{{appId}}"
                "name"        "{{name}}"
                "installdir"  "{{name}}"
                "StateFlags"  "4"
                "SizeOnDisk"  "{{sizeOnDisk}}"
                "LastUpdated" "{{Unix(updatedDaysAgo)}}"
            }
            """);
    }

    /// <summary>
    /// サイズは巨大だがサンプル可能な中身は小さいファイルを作る。
    /// 実環境で 20GB の exe から数 MB だけ読んで外挿する状況を再現している。
    /// </summary>
    private const int SampleContentSize = 512 * 1024;

    /// <summary>圧縮がよく効く中身（繰り返しパターン）を持つファイルを作る。</summary>
    private static void AddCompressibleFile(FakeFileSystem fs, string path, long logicalSize)
    {
        var content = new byte[SampleContentSize];
        var pattern = System.Text.Encoding.UTF8.GetBytes(
            "MZ\x90\x00\x03\x00\x00\x00 This program cannot be run in DOS mode. ");

        for (var i = 0; i < content.Length; i++) content[i] = pattern[i % pattern.Length];

        fs.AddFile(path, content, logicalSize: logicalSize);
    }

    /// <summary>圧縮が効かない中身（乱数）を持つファイルを作る。</summary>
    private static void AddIncompressibleFile(FakeFileSystem fs, string path, long logicalSize)
    {
        var content = new byte[SampleContentSize];
        new Random(path.Length).NextBytes(content);
        fs.AddFile(path, content, logicalSize: logicalSize);
    }

    private static ScanResult Scan(FakeFileSystem fs)
        => new LibraryScanner(fs, timeProvider: new FixedTimeProvider(Now)).Scan(SteamRoot);

    [Fact]
    public void ライブラリ全体を走査して全タイトルを評価する()
    {
        var result = Scan(BuildLibrary());

        Assert.Equal(4, result.Assessments.Count);
        Assert.False(result.PlayHistoryUnavailable);
        Assert.Single(result.Users);
    }

    // -----------------------------------------------------------------
    // ReadTitles — 起動直後に「空の画面」を見せないための軽量な一覧
    // -----------------------------------------------------------------

    private static IReadOnlyList<TitleSummary> ReadTitles(FakeFileSystem fs)
        => new LibraryScanner(fs, timeProvider: new FixedTimeProvider(Now)).ReadTitles(SteamRoot);

    [Fact]
    public void タイトル一覧はサイズと最終プレイを返す()
    {
        var titles = ReadTitles(BuildLibrary());

        Assert.Equal(4, titles.Count);

        var movie = titles.Single(t => t.Name == "Movie Game");
        Assert.Equal(60 * GiB, movie.SizeBytes);
        Assert.Equal(1100, movie.DaysSincePlayed);
        Assert.True(movie.IsFullyInstalled);
    }

    [Fact]
    public void タイトル一覧はサイズの大きい順に並ぶ()
    {
        // 起動直後に「効きそうなもの」が上に来ていてほしい
        var titles = ReadTitles(BuildLibrary());

        Assert.Equal(
            titles.Select(t => t.SizeBytes).OrderByDescending(s => s),
            titles.Select(t => t.SizeBytes));
    }

    [Fact]
    public void タイトル一覧は圧縮率の推定を行わない()
    {
        // 推定は 100GB 級の走査を伴うため、一覧取得では絶対に走らせない。
        // プローブが呼ばれたら失敗するようにして保証する
        var probe = new ThrowingProbe();
        var scanner = new LibraryScanner(
            BuildLibrary(), probe: probe, timeProvider: new FixedTimeProvider(Now));

        var titles = scanner.ReadTitles(SteamRoot);

        Assert.Equal(4, titles.Count);
        Assert.False(probe.WasCalled);
    }

    [Fact]
    public void タイトル一覧は起動記録が無ければ日数をnullにする()
    {
        // 「記録が無い」を「一度も遊んでいない」と断定しない（AGENTS.md）
        var fs = BuildLibrary();
        AddApp(fs, 500, "No History", 5 * GiB, updatedDaysAgo: 10);
        AddCompressibleFile(fs, @"D:\SteamLibrary\steamapps\common\No History\game.exe", 5 * GiB);

        var title = ReadTitles(fs).Single(t => t.Name == "No History");

        Assert.Null(title.DaysSincePlayed);
        Assert.Null(title.LastPlayed);
    }

    private sealed class ThrowingProbe : ICompressibilityProbe
    {
        public bool WasCalled { get; private set; }

        public double Measure(ReadOnlySpan<byte> data)
        {
            WasCalled = true;
            throw new InvalidOperationException("一覧取得で圧縮率の推定を行ってはならない");
        }
    }

    [Fact]
    public void 圧縮しやすく更新が落ち着いたタイトルは圧縮推奨になる()
    {
        var result = Scan(BuildLibrary());
        var game = result.Assessments.Single(a => a.Name == "Archive Game");

        Assert.Equal(AdviceKind.Compress, game.Advice);
        Assert.True(game.Estimate.Measured);
        Assert.True(game.Estimate.SavedFraction > 0.5,
            $"実行ファイル中心なら半分以上縮むはず (実際 {game.Estimate.SavedFraction:P0})");
    }

    [Fact]
    public void 動画だらけで長期未起動なら削除候補として提示される()
    {
        var result = Scan(BuildLibrary());
        var game = result.Assessments.Single(a => a.Name == "Movie Game");

        Assert.Equal(AdviceKind.NotWorthCompressing, game.Advice);
        Assert.True(game.IsUninstallCandidate);
        Assert.Contains(ReasonCode.LongUnplayed, game.Reasons);
        Assert.Contains(ReasonCode.DeleteYieldsMuchMore, game.Reasons);
    }

    [Fact]
    public void DirectStorage対応タイトルは自動で除外される()
    {
        var result = Scan(BuildLibrary());
        var game = result.Assessments.Single(a => a.Name == "Fast Loader");

        Assert.Equal(AdviceKind.DoNotCompress, game.Advice);
        Assert.Contains(ReasonCode.DirectStorageDetected, game.Reasons);
    }

    [Fact]
    public void 更新が活発なタイトルは自動再圧縮つきになる()
    {
        var result = Scan(BuildLibrary());
        var game = result.Assessments.Single(a => a.Name == "Live Service");

        Assert.Equal(AdviceKind.CompressWithWatcher, game.Advice);
    }

    [Fact]
    public void 削減見込みの大きい順に並ぶ()
    {
        var result = Scan(BuildLibrary());

        var saved = result.Assessments.Select(a => a.Estimate.EstimatedSavedBytes).ToList();

        Assert.Equal(saved.OrderByDescending(s => s).ToList(), saved);
    }

    [Fact]
    public void 合計値は圧縮対象のみを積み上げる()
    {
        var result = Scan(BuildLibrary());

        var expected = result.Assessments
            .Where(a => a.Advice is AdviceKind.Compress
                                 or AdviceKind.CompressWithWatcher
                                 or AdviceKind.CompressWithCaution)
            .Sum(a => a.Estimate.EstimatedSavedBytes);

        Assert.Equal(expected, result.TotalEstimatedSavingBytes);
        Assert.True(result.TotalEstimatedSavingBytes > 0);

        // DirectStorage タイトルの分は合計に混ぜない
        var fastLoader = result.Assessments.Single(a => a.Name == "Fast Loader");
        Assert.True(result.TotalEstimatedSavingBytes < result.Assessments.Sum(a => a.Estimate.EstimatedSavedBytes)
                    || fastLoader.Estimate.EstimatedSavedBytes == 0);
    }

    [Fact]
    public void プレイ履歴が無い環境でも走査は成立する()
    {
        var fs = BuildLibrary();
        var result = new LibraryScanner(
            new FakeFileSystemWithoutUserdata(fs),
            timeProvider: new FixedTimeProvider(Now)).Scan(SteamRoot);

        Assert.True(result.PlayHistoryUnavailable);
        Assert.Equal(4, result.Assessments.Count);

        // 履歴不明なら全タイトルが「未起動」扱いになる
        Assert.True(result.Assessments.All(a => a.Reasons.Contains(ReasonCode.NeverPlayed)));
    }

    [Fact]
    public void NTFS以外のドライブは全タイトルが圧縮非推奨になる()
    {
        var fs = BuildLibrary();
        fs.VolumeFileSystems[@"D:\"] = "ReFS";

        var result = Scan(fs);

        Assert.True(result.Assessments.All(a => a.Advice == AdviceKind.DoNotCompress));
        Assert.True(result.Assessments.All(a => a.Reasons.Contains(ReasonCode.NotNtfs)));
    }

    [Fact]
    public void 走査は一切書き込みを行わない()
    {
        // Phase 0 を無害だと言い切るための保証。
        // 「解析するだけ」と説明したツールがファイルを触っていたら信用は終わる
        var fs = BuildLibrary();

        var before = fs.EnumerateFilesRecursive(@"D:\SteamLibrary")
            .ToDictionary(f => f, f => (fs.GetFileSize(f), fs.GetFileSizeOnDisk(f)));

        Scan(fs);

        var after = fs.EnumerateFilesRecursive(@"D:\SteamLibrary")
            .ToDictionary(f => f, f => (fs.GetFileSize(f), fs.GetFileSizeOnDisk(f)));

        Assert.Equal(before.Count, after.Count);
        foreach (var (path, sizes) in before) Assert.Equal(sizes, after[path]);
    }

    /// <summary>userdata だけを隠すラッパー。</summary>
    private sealed class FakeFileSystemWithoutUserdata(FakeFileSystem inner) : IFileSystem
    {
        private static bool IsUserdata(string path)
            => path.Contains(@"\userdata\", StringComparison.OrdinalIgnoreCase);

        public bool FileExists(string path) => !IsUserdata(path) && inner.FileExists(path);

        public bool DirectoryExists(string path) => !IsUserdata(path) && inner.DirectoryExists(path);

        public string ReadAllText(string path) => inner.ReadAllText(path);

        public Stream? OpenRead(string path) => inner.OpenRead(path);

        public IEnumerable<string> EnumerateFiles(string d, string p) => inner.EnumerateFiles(d, p);

        public IEnumerable<string> EnumerateFilesRecursive(string d) => inner.EnumerateFilesRecursive(d);

        public IEnumerable<string> EnumerateDirectories(string d)
            => IsUserdata(d + @"\") ? [] : inner.EnumerateDirectories(d);

        public long GetFileSize(string path) => inner.GetFileSize(path);

        public long GetFileSizeOnDisk(string path) => inner.GetFileSizeOnDisk(path);

        public DateTimeOffset GetLastWriteTimeUtc(string path) => inner.GetLastWriteTimeUtc(path);

        public string Combine(params string[] parts) => inner.Combine(parts);

        public string GetFileName(string path) => inner.GetFileName(path);

        public string GetExtension(string path) => inner.GetExtension(path);

        public string? GetVolumeRoot(string path) => inner.GetVolumeRoot(path);

        public string? GetFileSystemName(string path) => inner.GetFileSystemName(path);

        public bool IsReparsePoint(string path) => inner.IsReparsePoint(path);

        public long? GetAvailableFreeBytes(string path) => inner.GetAvailableFreeBytes(path);
    }
}
