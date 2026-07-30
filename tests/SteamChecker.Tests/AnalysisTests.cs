using SteamChecker.Core.Analysis;

namespace SteamChecker.Tests;

public class FileCategoryTests
{
    [Theory]
    [InlineData(".exe", FileCategory.Executable)]
    [InlineData(".DLL", FileCategory.Executable)]
    [InlineData(".json", FileCategory.Text)]
    [InlineData(".uasset", FileCategory.RawAsset)]
    [InlineData(".dds", FileCategory.BlockCompressedTexture)]
    [InlineData(".mp4", FileCategory.CompressedMedia)]
    [InlineData(".wem", FileCategory.CompressedMedia)]
    [InlineData(".zip", FileCategory.CompressionFormat)]
    [InlineData(".arc", FileCategory.GameArchive)]
    [InlineData(".rpf", FileCategory.GameArchive)]
    public void 拡張子からカテゴリを判定する(string extension, FileCategory expected)
        => Assert.Equal(expected, FileCategories.FromExtension(extension));

    [Theory]
    [InlineData(".pak")]
    [InlineData(".dat")]
    [InlineData(".bin")]
    [InlineData(".assets")]
    public void 中身が読めない拡張子はUnknownに落とす(string extension)
    {
        // .pak などは中身が圧縮済みかどうかがタイトル依存。
        // 拡張子で決め打ちすると見積もりを外すので、実測に委ねる
        Assert.Equal(FileCategory.Unknown, FileCategories.FromExtension(extension));
    }

    [Fact]
    public void 圧縮済みメディアの事前値はほぼ1である()
    {
        Assert.InRange(FileCategories.PriorRatio(FileCategory.CompressedMedia), 0.95, 1.0);
        Assert.InRange(FileCategories.PriorRatio(FileCategory.CompressionFormat), 0.95, 1.0);
    }

    [Fact]
    public void テキストの事前値が最も小さい()
    {
        var text = FileCategories.PriorRatio(FileCategory.Text);

        foreach (FileCategory category in Enum.GetValues<FileCategory>())
        {
            if (category == FileCategory.Text) continue;
            Assert.True(text <= FileCategories.PriorRatio(category), $"{category} より小さいはず");
        }
    }
}

public class FeatureDetectorTests
{
    [Fact]
    public void DirectStorageのDLLを検出する()
    {
        var detector = new FeatureDetector();
        detector.Feed("game.exe");
        detector.Feed("dstorage.dll");

        Assert.True(detector.Result.HasFlag(GameFeatures.DirectStorage));
    }

    [Fact]
    public void 大文字小文字を無視して検出する()
    {
        var detector = new FeatureDetector();
        detector.Feed("DStorageCore.DLL");

        Assert.True(detector.Result.HasFlag(GameFeatures.DirectStorage));
    }

    [Fact]
    public void EasyAntiCheatを検出する()
    {
        var detector = new FeatureDetector();
        detector.Feed("EasyAntiCheat_x64.dll");

        Assert.True(detector.Result.HasFlag(GameFeatures.EasyAntiCheat));
        Assert.True(detector.Result.HasAnyAntiCheat());
    }

    [Fact]
    public void BattlEyeを検出する()
    {
        var detector = new FeatureDetector();
        detector.Feed("BEService_x64.exe");

        Assert.True(detector.Result.HasFlag(GameFeatures.BattlEye));
        Assert.True(detector.Result.HasAnyAntiCheat());
    }

    [Fact]
    public void 何もなければフラグは立たない()
    {
        var detector = new FeatureDetector();
        detector.Feed("game.exe");
        detector.Feed("data.pak");

        Assert.Equal(GameFeatures.None, detector.Result);
        Assert.False(detector.Result.HasAnyAntiCheat());
    }

    [Fact]
    public void 部分一致では誤検出しない()
    {
        // "mydstorage.dll" のようなファイルで誤検出すると、
        // 圧縮できるはずのタイトルが不当に除外される
        var detector = new FeatureDetector();
        detector.Feed("mydstorage.dll");
        detector.Feed("dstorage.dll.bak");

        Assert.False(detector.Result.HasFlag(GameFeatures.DirectStorage));
    }
}

public class FolderProfilerTests
{
    private const string GameDir = @"D:\SteamLibrary\steamapps\common\TestGame";

    [Fact]
    public void カテゴリ別にバイト数を集計する()
    {
        var fs = new FakeFileSystem()
            .AddSizedFile($@"{GameDir}\game.exe", 100_000_000)
            .AddSizedFile($@"{GameDir}\movies\intro.mp4", 400_000_000)
            .AddSizedFile($@"{GameDir}\config\settings.json", 1_000_000);

        var profile = new FolderProfiler(fs).Profile(GameDir);

        Assert.Equal(501_000_000, profile.TotalLogicalBytes);
        Assert.Equal(3, profile.FileCount);
        Assert.Equal(100_000_000, profile.BytesByCategory[FileCategory.Executable]);
        Assert.Equal(400_000_000, profile.BytesByCategory[FileCategory.CompressedMedia]);
    }

    [Fact]
    public void 動画だらけのフォルダはヒューリスティックでもほぼ縮まないと出る()
    {
        var fs = new FakeFileSystem()
            .AddSizedFile($@"{GameDir}\movies\a.mp4", 9_000_000_000)
            .AddSizedFile($@"{GameDir}\game.exe", 1_000_000_000);

        var profile = new FolderProfiler(fs).Profile(GameDir);

        // 90% が動画なので削減見込みは 1 割未満
        Assert.InRange(profile.HeuristicRatio(), 0.90, 1.0);
    }

    [Fact]
    public void 実行ファイル中心のフォルダはよく縮むと出る()
    {
        var fs = new FakeFileSystem()
            .AddSizedFile($@"{GameDir}\game.exe", 5_000_000_000)
            .AddSizedFile($@"{GameDir}\data.json", 5_000_000_000);

        var profile = new FolderProfiler(fs).Profile(GameDir);

        Assert.InRange(profile.HeuristicRatio(), 0.30, 0.42);
    }

    [Fact]
    public void 走査中に特徴フラグも同時に集める()
    {
        var fs = new FakeFileSystem()
            .AddSizedFile($@"{GameDir}\game.exe", 1_000_000)
            .AddSizedFile($@"{GameDir}\dstorage.dll", 500_000)
            .AddSizedFile($@"{GameDir}\EasyAntiCheat\EasyAntiCheat_x64.dll", 300_000);

        var profile = new FolderProfiler(fs).Profile(GameDir);

        Assert.True(profile.Features.HasFlag(GameFeatures.DirectStorage));
        Assert.True(profile.Features.HasFlag(GameFeatures.EasyAntiCheat));
    }

    [Fact]
    public void 既に圧縮済みなら検出する()
    {
        // 論理 10GB に対して実占有 5GB → 既に WOF 圧縮が効いている
        var fs = new FakeFileSystem()
            .AddSizedFile($@"{GameDir}\game.exe", 10_000_000_000, sizeOnDisk: 5_000_000_000);

        var profile = new FolderProfiler(fs).Profile(GameDir);

        Assert.True(profile.Features.HasFlag(GameFeatures.AlreadyCompressed));
        Assert.InRange(profile.PhysicalToLogicalRatio, 0.49, 0.51);
    }

    [Fact]
    public void 未圧縮なら圧縮済みフラグは立たない()
    {
        var fs = new FakeFileSystem()
            .AddSizedFile($@"{GameDir}\game.exe", 10_000_000_000, sizeOnDisk: 10_000_000_000);

        var profile = new FolderProfiler(fs).Profile(GameDir);

        Assert.False(profile.Features.HasFlag(GameFeatures.AlreadyCompressed));
    }

    [Fact]
    public void ファイル数上限に達したら打ち切りを報告する()
    {
        var fs = new FakeFileSystem();
        for (var i = 0; i < 50; i++) fs.AddSizedFile($@"{GameDir}\f{i}.dat", 1000);

        var profile = new FolderProfiler(fs, maxFiles: 10).Profile(GameDir);

        Assert.True(profile.Truncated);
        Assert.Equal(10, profile.FileCount);
    }

    [Fact]
    public void 空フォルダでも例外を出さない()
    {
        var profile = new FolderProfiler(new FakeFileSystem()).Profile(GameDir);

        Assert.Equal(0, profile.TotalLogicalBytes);
        Assert.Equal(1.0, profile.HeuristicRatio());
    }
}
