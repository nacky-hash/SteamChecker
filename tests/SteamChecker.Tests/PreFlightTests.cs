using SteamChecker.Core.Compression;
using SteamChecker.Core.Steam;

namespace SteamChecker.Tests;

/// <summary>
/// 書き込み前の安全検査。方針は fail-closed —
/// 判定できないものを「安全」と扱った瞬間にこの検査は意味を失う。
/// </summary>
public class PreFlightTests
{
    private const long GiB = 1024L * 1024 * 1024;

    private static readonly SteamLibrary Library = new() { Path = @"C:\Steam" };

    private static FakeFileSystem BuildFs()
    {
        var fs = new FakeFileSystem();
        fs.AddTextFile(@"C:\Steam\steamapps\appmanifest_10.acf", "\"AppState\" {}");
        fs.AddFile(@"C:\Steam\steamapps\common\Game\game.exe", new byte[1024]);
        fs.AddSizedFile(@"C:\Steam\steamapps\common\Game\data.pak", 4 * GiB);
        return fs;
    }

    private static InstalledApp App(string installDir = "Game", string? fullPath = null) => new()
    {
        AppId = 10,
        Name = "Game",
        InstallDir = installDir,
        FullPath = fullPath ?? @"C:\Steam\steamapps\common\" + installDir,
        Library = Library,
    };

    private static PreFlightChecker Checker(
        FakeFileSystem fs,
        IReadOnlyCollection<string>? processes = null,
        Func<string, bool>? inUse = null)
        => new(fs,
            runningProcessNames: () => processes ?? [],
            isFileInUse: inUse ?? (_ => false));

    // -----------------------------------------------------------------
    // 正常系
    // -----------------------------------------------------------------

    [Fact]
    public void 問題がなければ通す()
    {
        var report = Checker(BuildFs()).Check(App());

        Assert.True(report.CanProceed);
        Assert.Empty(report.Issues);
    }

    // -----------------------------------------------------------------
    // ライブラリ配下の検証（パストラバーサル）
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(@"..\..\Windows")]
    [InlineData(@"../../etc")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"sub\dir")]
    [InlineData("..")]
    public void 危険なinstalldirはブロックする(string installDir)
    {
        var report = Checker(BuildFs()).Check(App(installDir));

        Assert.False(report.CanProceed);
        Assert.Contains(report.Issues,
            i => i.CheckId == PreFlightCheckId.TargetInsideLibrary && i.Blocks);
    }

    [Fact]
    public void FullPathがライブラリ外を指していればブロックする()
    {
        // installdir は正常でも、組み立て済み FullPath が別の場所を指すケース
        var fs = BuildFs();
        fs.AddFile(@"C:\Windows\System32\config.sys", new byte[16]);

        var report = Checker(fs).Check(App(fullPath: @"C:\Windows\System32"));

        Assert.False(report.CanProceed);
        Assert.Contains(report.Issues,
            i => i.CheckId == PreFlightCheckId.TargetInsideLibrary && i.Blocks);
    }

    // -----------------------------------------------------------------
    // TOCTOU
    // -----------------------------------------------------------------

    [Fact]
    public void manifestが消えていればブロックする()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"C:\Steam\steamapps\common\Game\game.exe", new byte[1024]);
        // appmanifest_10.acf を意図的に置かない（走査後のアンインストールを再現）

        var report = Checker(fs).Check(App());

        Assert.False(report.CanProceed);
        Assert.Contains(report.Issues,
            i => i.CheckId == PreFlightCheckId.ManifestStillExists && i.Blocks);
    }

    [Fact]
    public void 対象フォルダが消えていればブロックする()
    {
        var fs = new FakeFileSystem();
        fs.AddTextFile(@"C:\Steam\steamapps\appmanifest_10.acf", "\"AppState\" {}");

        var report = Checker(fs).Check(App());

        Assert.False(report.CanProceed);
        Assert.Contains(report.Issues,
            i => i.CheckId == PreFlightCheckId.TargetExists && i.Blocks);
    }

    // -----------------------------------------------------------------
    // ファイルシステム
    // -----------------------------------------------------------------

    [Fact]
    public void NTFS以外はブロックする()
    {
        var fs = BuildFs();
        fs.VolumeFileSystems[@"C:\"] = "exFAT";

        var report = Checker(fs).Check(App());

        Assert.False(report.CanProceed);
        Assert.Contains(report.Issues,
            i => i.CheckId == PreFlightCheckId.NtfsFileSystem && i.Blocks);
    }

    [Fact]
    public void ファイルシステムが判定できない場合もブロックする()
    {
        // scan は fail-open（判定不能を NTFS とみなす）だが、書き込み前は逆に倒す
        var fs = BuildFs();
        fs.VolumeFileSystems.Remove(@"C:\");

        var report = Checker(fs).Check(App());

        Assert.False(report.CanProceed);
        Assert.Contains(report.Issues,
            i => i.CheckId == PreFlightCheckId.NtfsFileSystem && i.Blocks);
    }

    // -----------------------------------------------------------------
    // reparse point（ジャンクション / シンボリックリンク）
    // -----------------------------------------------------------------

    [Fact]
    public void 対象フォルダ自体がジャンクションならブロックする()
    {
        var fs = BuildFs();
        fs.MarkReparsePoint(@"C:\Steam\steamapps\common\Game");

        var report = Checker(fs).Check(App());

        Assert.False(report.CanProceed);
        Assert.Contains(report.Issues,
            i => i.CheckId == PreFlightCheckId.NoReparsePoint && i.Blocks);
    }

    [Fact]
    public void 親階層のジャンクションもブロックする()
    {
        var fs = BuildFs();
        fs.MarkReparsePoint(@"C:\Steam\steamapps");

        var report = Checker(fs).Check(App());

        Assert.False(report.CanProceed);
        Assert.Contains(report.Issues,
            i => i.CheckId == PreFlightCheckId.NoReparsePoint && i.Blocks);
    }

    [Fact]
    public void 子階層のジャンクションもブロックする()
    {
        var fs = BuildFs();
        fs.AddFile(@"C:\Steam\steamapps\common\Game\mods\linked\readme.txt", new byte[16]);
        fs.MarkReparsePoint(@"C:\Steam\steamapps\common\Game\mods\linked");

        var report = Checker(fs).Check(App());

        Assert.False(report.CanProceed);
        Assert.Contains(report.Issues,
            i => i.CheckId == PreFlightCheckId.NoReparsePoint && i.Blocks);
    }

    // -----------------------------------------------------------------
    // プロセス
    // -----------------------------------------------------------------

    [Fact]
    public void Steamが起動中ならブロックする()
    {
        var report = Checker(BuildFs(), processes: ["explorer", "Steam"]).Check(App());

        Assert.False(report.CanProceed);
        Assert.Contains(report.Issues,
            i => i.CheckId == PreFlightCheckId.SteamNotRunning && i.Blocks);
    }

    [Fact]
    public void プロセス一覧が取得できない場合はブロックする()
    {
        var checker = new PreFlightChecker(BuildFs(), runningProcessNames: null, isFileInUse: _ => false);

        var report = checker.Check(App());

        Assert.False(report.CanProceed);
        Assert.Contains(report.Issues,
            i => i.CheckId == PreFlightCheckId.SteamNotRunning && i.Blocks);
    }

    [Fact]
    public void ゲームのexeが使用中ならブロックする()
    {
        var report = Checker(BuildFs(), inUse: path => path.EndsWith(".exe")).Check(App());

        Assert.False(report.CanProceed);
        Assert.Contains(report.Issues,
            i => i.CheckId == PreFlightCheckId.GameNotRunning && i.Blocks);
    }

    // -----------------------------------------------------------------
    // DirectStorage（Warn であって Block ではない。「兆候なし＝安全」とも言わない）
    // -----------------------------------------------------------------

    [Fact]
    public void DirectStorageの兆候は警告として出す()
    {
        var fs = BuildFs();
        fs.AddFile(@"C:\Steam\steamapps\common\Game\dstorage.dll", new byte[1024]);

        var report = Checker(fs).Check(App());

        Assert.True(report.CanProceed);
        Assert.True(report.HasWarnings);
        Assert.Contains(report.Issues,
            i => i.CheckId == PreFlightCheckId.DirectStorageSigns && !i.Blocks);
    }

    // -----------------------------------------------------------------
    // 容量
    // -----------------------------------------------------------------

    [Fact]
    public void 空き容量が足りなければブロックする()
    {
        var fs = BuildFs();
        fs.VolumeFreeBytes[@"C:\"] = 1024; // ほぼゼロ

        var report = Checker(fs).Check(App());

        Assert.False(report.CanProceed);
        Assert.Contains(report.Issues,
            i => i.CheckId == PreFlightCheckId.EnoughFreeSpace && i.Blocks);
    }

    [Fact]
    public void 最大ファイルサイズと余裕分の空きがあれば通す()
    {
        var fs = BuildFs();
        // 最大ファイル 4GiB + 余裕 256MB より少し多い空き
        fs.VolumeFreeBytes[@"C:\"] = 4 * GiB + 512L * 1024 * 1024;

        var report = Checker(fs).Check(App());

        Assert.True(report.CanProceed);
    }
}
