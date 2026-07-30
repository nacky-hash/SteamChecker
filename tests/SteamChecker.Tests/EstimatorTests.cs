using SteamChecker.Core.Analysis;

namespace SteamChecker.Tests;

public class ChunkedBrotliProbeTests
{
    private readonly ChunkedBrotliProbe _probe = new();

    [Fact]
    public void ゼロ埋めデータは極端に縮む()
    {
        var data = new byte[1024 * 1024];

        Assert.InRange(_probe.Measure(data), 0.0, 0.02);
    }

    [Fact]
    public void 乱数データはほぼ縮まない()
    {
        var data = new byte[1024 * 1024];
        new Random(42).NextBytes(data);

        // 縮まないチャンクは非圧縮で格納される想定なので 1.0 を超えない
        Assert.InRange(_probe.Measure(data), 0.95, 1.0);
    }

    [Fact]
    public void テキストはよく縮む()
    {
        var text = string.Concat(Enumerable.Repeat(
            "public static void Main(string[] args) { Console.WriteLine(\"hello\"); }\n", 4000));
        var data = System.Text.Encoding.UTF8.GetBytes(text);

        Assert.InRange(_probe.Measure(data), 0.0, 0.15);
    }

    [Fact]
    public void 空データは1を返す()
        => Assert.Equal(1.0, _probe.Measure([]));

    [Fact]
    public void チャンク境界を跨ぐ繰り返しでは全体圧縮より控えめな値になる()
    {
        // WOF の LZX は 32KiB チャンク単位で独立に圧縮するため、
        // チャンクを跨ぐ長距離の繰り返しは利用できない。
        // ファイル全体を 1 ストリームで圧縮した値で見積もると
        // 実際より良い数字が出てユーザーを裏切ることになる。
        var block = new byte[64 * 1024];
        new Random(7).NextBytes(block);

        // 同じ 64KiB ブロックを 16 回繰り返す = 全体では極めて冗長
        var data = new byte[block.Length * 16];
        for (var i = 0; i < 16; i++) Array.Copy(block, 0, data, i * block.Length, block.Length);

        var chunked = new ChunkedBrotliProbe(chunkSize: 32 * 1024).Measure(data);
        var wholeFile = new ChunkedBrotliProbe(chunkSize: data.Length).Measure(data);

        // ストリーム全体を見れば 1/16 近くまで縮むが、32KiB 区切りでは縮まない
        Assert.True(wholeFile < 0.2, $"全体圧縮なら大幅に縮むはず (実際 {wholeFile:F3})");
        Assert.True(chunked > 0.9, $"32KiB チャンクではほぼ縮まないはず (実際 {chunked:F3})");
    }

    [Fact]
    public void 補正係数が結果に反映される()
    {
        var text = System.Text.Encoding.UTF8.GetBytes(
            string.Concat(Enumerable.Repeat("abcdefghij", 20000)));

        var baseline = new ChunkedBrotliProbe(calibration: 1.0).Measure(text);
        var adjusted = new ChunkedBrotliProbe(calibration: 1.5).Measure(text);

        Assert.InRange(adjusted, baseline * 1.49, baseline * 1.51);
    }

    [Fact]
    public void 結果は常に0から1の範囲に収まる()
    {
        var random = new Random(1);

        for (var i = 0; i < 20; i++)
        {
            var data = new byte[random.Next(1, 200_000)];
            random.NextBytes(data);

            Assert.InRange(_probe.Measure(data), 0.0, 1.0);
        }
    }
}

/// <summary>比率を固定で返すテスト用プローブ。</summary>
internal sealed class StubProbe(double ratio) : ICompressibilityProbe
{
    public int CallCount { get; private set; }

    public double Measure(ReadOnlySpan<byte> data)
    {
        CallCount++;
        return ratio;
    }
}

public class SamplingEstimatorTests
{
    private const string GameDir = @"D:\SteamLibrary\steamapps\common\TestGame";

    private static readonly SamplingOptions FastOptions = new()
    {
        TotalSampleBudgetBytes = 4 * 1024 * 1024,
        MinPerCategoryBytes = 256 * 1024,
        MaxBytesPerFile = 256 * 1024,
        MinFileSizeBytes = 1024,
        MaxFilesPerCategory = 8,
    };

    [Fact]
    public void 空フォルダなら比率1を返す()
    {
        var fs = new FakeFileSystem();
        var profile = new FolderProfiler(fs).Profile(GameDir);

        var estimate = new SamplingEstimator(fs, new StubProbe(0.5), FastOptions).Estimate(profile);

        Assert.Equal(1.0, estimate.Ratio);
        Assert.False(estimate.Measured);
        Assert.Equal(0, estimate.EstimatedSavedBytes);
    }

    [Fact]
    public void 実測できた場合はMeasuredがtrueになる()
    {
        var fs = new FakeFileSystem()
            .AddFile($@"{GameDir}\game.exe", new byte[200_000]);

        var profile = new FolderProfiler(fs).Profile(GameDir);
        var estimate = new SamplingEstimator(fs, new StubProbe(0.4), FastOptions).Estimate(profile);

        Assert.True(estimate.Measured);
        Assert.InRange(estimate.Ratio, 0.39, 0.41);
        Assert.True(estimate.SampledFiles > 0);
    }

    [Fact]
    public void 圧縮済みメディアは実測せず事前値を使う()
    {
        // 動画を毎回サンプリングして読むのは時間の無駄。
        // 100GB のゲームで数分待たされるようなツールは使われない
        var fs = new FakeFileSystem()
            .AddFile($@"{GameDir}\movie.mp4", new byte[500_000]);

        var probe = new StubProbe(0.1);
        var profile = new FolderProfiler(fs).Profile(GameDir);
        var estimate = new SamplingEstimator(fs, probe, FastOptions).Estimate(profile);

        Assert.Equal(0, probe.CallCount);
        Assert.False(estimate.Measured);
        Assert.InRange(estimate.Ratio, 0.95, 1.0);
    }

    [Fact]
    public void ゲーム独自コンテナは名前がアーカイブでも必ず実測する()
    {
        // 回帰テスト。
        // かつて .arc / .rpf / .vpk 等を「アーカイブだから縮まない」と決め打ち、
        // 実測を省いて事前値 0.98 を使っていた。
        //
        // 実機で測ったところ theHunter: Call of the Wild の .arc は LZX 29.4% で、
        // 117GB のうち約 82GB が削減できた。決め打ちでは「2.38GB」と表示され、
        // 34 倍過小に見積もっていた。
        //
        // 「拡張子では中身が圧縮済みか判断できない」がこのツールの前提である以上、
        // 名前がアーカイブというだけで実測を省いてはいけない。
        var fs = new FakeFileSystem()
            .AddFile($@"{GameDir}\data.arc", new byte[500_000]);

        var probe = new StubProbe(0.29);
        var profile = new FolderProfiler(fs).Profile(GameDir);
        var estimate = new SamplingEstimator(fs, probe, FastOptions).Estimate(profile);

        Assert.True(probe.CallCount > 0, "独自コンテナは実測しなければならない");
        Assert.True(estimate.Measured);
        Assert.InRange(estimate.Ratio, 0.28, 0.30);
    }

    [Fact]
    public void 圧縮フォーマットそのものは実測せず事前値を使う()
    {
        // .zip / .7z / .gz は定義上すでに圧縮済みなので、実測する価値がない。
        // 実測を省いてよいのはこのカテゴリだけ。
        var fs = new FakeFileSystem()
            .AddFile($@"{GameDir}\assets.zip", new byte[500_000]);

        var probe = new StubProbe(0.1);
        var profile = new FolderProfiler(fs).Profile(GameDir);
        var estimate = new SamplingEstimator(fs, probe, FastOptions).Estimate(profile);

        Assert.Equal(0, probe.CallCount);
        Assert.InRange(estimate.Ratio, 0.95, 1.0);
    }

    [Fact]
    public void カテゴリ別の実測値をバイト数で重み付けして合成する()
    {
        // exe 100KB（実測 0.2）+ mp4 900KB（事前値 0.98）
        // → 0.2*0.1 + 0.98*0.9 = 0.902
        var fs = new FakeFileSystem()
            .AddFile($@"{GameDir}\game.exe", new byte[100_000])
            .AddFile($@"{GameDir}\movie.mp4", new byte[900_000]);

        var profile = new FolderProfiler(fs).Profile(GameDir);
        var estimate = new SamplingEstimator(fs, new StubProbe(0.2), FastOptions).Estimate(profile);

        Assert.InRange(estimate.Ratio, 0.89, 0.91);
    }

    [Fact]
    public void 削減バイト数は比率と論理サイズから計算される()
    {
        var fs = new FakeFileSystem().AddFile($@"{GameDir}\game.exe", new byte[1_000_000]);

        var profile = new FolderProfiler(fs).Profile(GameDir);
        var estimate = new SamplingEstimator(fs, new StubProbe(0.25), FastOptions).Estimate(profile);

        Assert.InRange(estimate.EstimatedSavedBytes, 740_000, 760_000);
        Assert.InRange(estimate.SavedFraction, 0.74, 0.76);
    }

    [Fact]
    public void 小さすぎるファイルはサンプル対象外()
    {
        var fs = new FakeFileSystem().AddFile($@"{GameDir}\tiny.exe", new byte[100]);

        var probe = new StubProbe(0.1);
        var profile = new FolderProfiler(fs).Profile(GameDir);
        new SamplingEstimator(fs, probe, FastOptions).Estimate(profile);

        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public void 同じ入力なら何度実行しても同じ結果になる()
    {
        // 実行のたびに数字が変わると「さっきと違う」で信頼を失う。
        // サンプリング対象はパスのハッシュ順に決定的に選ぶ
        var fs = new FakeFileSystem();
        for (var i = 0; i < 30; i++)
        {
            var content = new byte[50_000];
            new Random(i).NextBytes(content);
            fs.AddFile($@"{GameDir}\data{i}.exe", content);
        }

        var profile = new FolderProfiler(fs).Profile(GameDir);
        var estimator = new SamplingEstimator(fs, new ChunkedBrotliProbe(), FastOptions);

        var first = estimator.Estimate(profile);
        var second = estimator.Estimate(profile);

        Assert.Equal(first.Ratio, second.Ratio);
        Assert.Equal(first.SampledFiles, second.SampledFiles);
        Assert.Equal(first.SampledBytes, second.SampledBytes);
    }

    [Fact]
    public void ユーザーのファイルに書き込まない()
    {
        // 見積もりのために圧縮を試し書きするツールもあるが、
        // 本ツールは読むだけ。Phase 0 を完全に無害にするための保証
        var fs = new FakeFileSystem().AddFile($@"{GameDir}\game.exe", new byte[200_000]);

        var before = fs.EnumerateFilesRecursive(GameDir)
            .ToDictionary(f => f, fs.GetFileSizeOnDisk);

        var profile = new FolderProfiler(fs).Profile(GameDir);
        new SamplingEstimator(fs, new ChunkedBrotliProbe(), FastOptions).Estimate(profile);

        var after = fs.EnumerateFilesRecursive(GameDir)
            .ToDictionary(f => f, fs.GetFileSizeOnDisk);

        Assert.Equal(before.Count, after.Count);
        foreach (var (path, size) in before) Assert.Equal(size, after[path]);
    }
}
