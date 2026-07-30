using System.Security.Cryptography;
using SteamChecker.Core;
using SteamChecker.Core.Compression;

namespace SteamChecker.Tests;

/// <summary>
/// 実 compact.exe を使う統合テスト。Windows 以外では何も検証せずに通る
/// （その場合の実効カバレッジは Windows 実機での実行に依存する。
/// テスト数の見かけに騙されないこと）。
/// </summary>
public class CompactExeEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "steamchecker-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private Dictionary<string, string> CreateSampleFiles()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));

        // 圧縮が効く内容（繰り返しテキスト）と効きにくい内容（乱数）を混ぜる
        var repeated = new byte[512 * 1024];
        for (var i = 0; i < repeated.Length; i++) repeated[i] = (byte)(i % 61);

        var random = new byte[256 * 1024];
        new Random(12345).NextBytes(random);

        File.WriteAllBytes(Path.Combine(_dir, "compressible.dat"), repeated);
        File.WriteAllBytes(Path.Combine(_dir, "random.bin"), random);
        File.WriteAllBytes(Path.Combine(_dir, "sub", "nested.dat"), repeated);

        return HashAll();
    }

    private Dictionary<string, string> HashAll()
        => Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)
            .ToDictionary(
                f => f,
                f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))),
                StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task 圧縮と解除の往復でファイル内容が完全に元に戻る()
    {
        if (!OperatingSystem.IsWindows()) return;

        var before = CreateSampleFiles();
        var fs = new PhysicalFileSystem();
        var engine = new CompactExeEngine(fs);

        var compress = await engine.CompressAsync(_dir, CompressionAlgorithm.Xpress4K);
        Assert.True(compress.Success, compress.ErrorMessage);

        var afterCompress = HashAll();
        Assert.Equal(before, afterCompress); // 透過圧縮は論理内容を変えない

        var restore = await engine.DecompressAsync(_dir);
        Assert.True(restore.Success, restore.ErrorMessage);

        var afterRestore = HashAll();
        Assert.Equal(before, afterRestore);
    }

    [Fact]
    public async Task キャンセルされてもファイルは消えず内容も変わらない()
    {
        if (!OperatingSystem.IsWindows()) return;

        var before = CreateSampleFiles();
        var fs = new PhysicalFileSystem();
        var engine = new CompactExeEngine(fs);

        // 開始直後にキャンセル（compact.exe の途中終了を再現）
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(1));

        var result = await engine.CompressAsync(
            _dir, CompressionAlgorithm.Xpress4K, ct: cts.Token);

        // 中断は「失敗」として報告される（成功と偽らない）
        // タイミングにより完走することもある — その場合 Success=true は正しい報告
        var after = HashAll();
        Assert.Equal(before.Count, after.Count);   // ファイルが消えていない
        Assert.Equal(before, after);               // 内容も変わっていない

        // 後始末: 部分的に圧縮された状態を解除して不変を再確認
        var restore = await engine.DecompressAsync(_dir);
        Assert.True(restore.Success, restore.ErrorMessage);
        Assert.Equal(before, HashAll());
    }

    [Fact]
    public async Task 存在しないフォルダは失敗として報告し例外を出さない()
    {
        var fs = new PhysicalFileSystem();
        var engine = new CompactExeEngine(fs, dryRun: true);

        var result = await engine.CompressAsync(
            Path.Combine(_dir, "does-not-exist"), CompressionAlgorithm.Lzx);

        Assert.False(result.Success);
    }
}
