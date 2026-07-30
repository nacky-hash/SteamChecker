using SteamChecker.Core.Steam;

namespace SteamChecker.Tests;

public class SteamReaderTests
{
    private const string SteamRoot = @"C:\Program Files (x86)\Steam";

    private static FakeFileSystem BuildFileSystem()
    {
        var fs = new FakeFileSystem();

        fs.AddTextFile($@"{SteamRoot}\steamapps\libraryfolders.vdf", """
            "libraryfolders"
            {
                "0"
                {
                    "path"       "C:\\Program Files (x86)\\Steam"
                    "label"      ""
                    "totalsize"  "0"
                }
                "1"
                {
                    "path"       "D:\\SteamLibrary"
                    "label"      "games"
                    "totalsize"  "2000398934016"
                }
                "2"
                {
                    "path"       "Z:\\Detached"
                }
            }
            """);

        fs.AddTextFile($@"{SteamRoot}\steamapps\appmanifest_220.acf", """
            "AppState"
            {
                "appid"       "220"
                "name"        "Half-Life 2"
                "installdir"  "Half-Life 2"
                "StateFlags"  "4"
                "SizeOnDisk"  "6120000000"
                "LastUpdated" "1700000000"
            }
            """);

        fs.AddSizedFile($@"{SteamRoot}\steamapps\common\Half-Life 2\hl2.exe", 8_000_000);

        fs.AddTextFile(@"D:\SteamLibrary\steamapps\appmanifest_1091500.acf", """
            "AppState"
            {
                "appid"       "1091500"
                "name"        "Cyberpunk 2077"
                "installdir"  "Cyberpunk 2077"
                "StateFlags"  "4"
                "SizeOnDisk"  "75000000000"
                "LastUpdated" "1750000000"
            }
            """);

        fs.AddSizedFile(@"D:\SteamLibrary\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe", 40_000_000);

        // 壊れた acf（appid 無し）— 黙って飛ばされること
        fs.AddTextFile(@"D:\SteamLibrary\steamapps\appmanifest_999.acf", """
            "AppState" { "name" "Broken" }
            """);

        return fs;
    }

    [Fact]
    public void 実在するライブラリだけを返す()
    {
        var reader = new SteamReader(BuildFileSystem());

        var libraries = reader.ReadLibraries(SteamRoot);

        Assert.Equal(2, libraries.Count);
        Assert.Contains(libraries, l => l.Path == SteamRoot);
        Assert.Contains(libraries, l => l.Path == @"D:\SteamLibrary");
        // Z:\Detached は steamapps が存在しないので除外される
        Assert.DoesNotContain(libraries, l => l.Path.StartsWith('Z'));
    }

    [Fact]
    public void ライブラリのラベルと容量を読む()
    {
        var reader = new SteamReader(BuildFileSystem());

        var d = reader.ReadLibraries(SteamRoot).Single(l => l.Path == @"D:\SteamLibrary");

        Assert.Equal("games", d.Label);
        Assert.Equal(2_000_398_934_016, d.TotalSize);
    }

    [Fact]
    public void 旧形式のlibraryfoldersを読める()
    {
        var fs = new FakeFileSystem();
        fs.AddTextFile($@"{SteamRoot}\steamapps\libraryfolders.vdf", """
            "LibraryFolders"
            {
                "TimeNextStatsReport"  "1600000000"
                "1"                    "D:\\SteamLibrary"
            }
            """);
        fs.AddSizedFile(@"D:\SteamLibrary\steamapps\dummy.txt", 1);

        var libraries = new SteamReader(fs).ReadLibraries(SteamRoot);

        Assert.Contains(libraries, l => l.Path == @"D:\SteamLibrary");
    }

    [Fact]
    public void appmanifestを読んでインストール情報を返す()
    {
        var fs = BuildFileSystem();
        var reader = new SteamReader(fs);
        var library = reader.ReadLibraries(SteamRoot).Single(l => l.Path == @"D:\SteamLibrary");

        var apps = reader.ReadInstalledApps(library);

        var cp = Assert.Single(apps);
        Assert.Equal(1091500, cp.AppId);
        Assert.Equal("Cyberpunk 2077", cp.Name);
        Assert.Equal(@"D:\SteamLibrary\steamapps\common\Cyberpunk 2077", cp.FullPath);
        Assert.True(cp.IsFullyInstalled);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1750000000), cp.LastUpdated);
    }

    [Fact]
    public void 壊れたappmanifestは黙って飛ばす()
    {
        var fs = BuildFileSystem();
        var reader = new SteamReader(fs);
        var library = reader.ReadLibraries(SteamRoot).Single(l => l.Path == @"D:\SteamLibrary");

        // appmanifest_999.acf は appid が無いので除外され、例外も出ない
        Assert.Single(reader.ReadInstalledApps(library));
    }

    [Theory]
    [InlineData(@"..\\..\\Windows\\System32")]
    [InlineData(@"C:\\Windows")]
    [InlineData("../../../etc")]
    [InlineData(@"sub\\dir")]
    public void パストラバーサルを含むinstalldirのmanifestは読み込まない(string installDir)
    {
        // installdir をそのまま Combine すると、破損・改ざんされた manifest 1 枚で
        // ライブラリ外の任意パスが走査・圧縮の対象になってしまう
        var fs = BuildFileSystem();
        fs.AddTextFile(@"D:\SteamLibrary\steamapps\appmanifest_666.acf", $$"""
            "AppState"
            {
                "appid"       "666"
                "name"        "Evil"
                "installdir"  "{{installDir}}"
                "StateFlags"  "4"
            }
            """);

        var reader = new SteamReader(fs);
        var library = reader.ReadLibraries(SteamRoot).Single(l => l.Path == @"D:\SteamLibrary");

        var apps = reader.ReadInstalledApps(library);

        Assert.DoesNotContain(apps, a => a.AppId == 666);
    }

    [Fact]
    public void localconfigからプレイ履歴を読む()
    {
        var fs = BuildFileSystem();
        fs.AddTextFile($@"{SteamRoot}\userdata\12345678\config\localconfig.vdf", """
            "UserLocalConfigStore"
            {
                "friends" { "PersonaName" "nacky" }
                "Software"
                {
                    "Valve"
                    {
                        "Steam"
                        {
                            "apps"
                            {
                                "220"     { "LastPlayed" "1500000000" "Playtime" "1200" }
                                "1091500" { "LastPlayed" "0" }
                            }
                        }
                    }
                }
            }
            """);

        var reader = new SteamReader(fs);
        var user = Assert.Single(reader.ReadUsers(SteamRoot));

        Assert.Equal(12345678, user.AccountId);
        Assert.Equal("nacky", user.PersonaName);

        var records = reader.ReadPlayRecords(user);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1500000000), records[220].LastPlayed);
        Assert.Equal(1200, records[220].PlaytimeMinutes);
        Assert.True(records[1091500].NeverPlayed);
    }

    [Fact]
    public void 複数アカウントのプレイ履歴は新しい方を採用する()
    {
        // 家族共有や複数アカウント環境で「実は遊ばれているのに未プレイ判定」になる事故を防ぐ
        var fs = BuildFileSystem();

        fs.AddTextFile($@"{SteamRoot}\userdata\111\config\localconfig.vdf", """
            "UserLocalConfigStore" { "Software" { "Valve" { "Steam" { "apps" {
                "220" { "LastPlayed" "1500000000" "Playtime" "100" }
            } } } } }
            """);

        fs.AddTextFile($@"{SteamRoot}\userdata\222\config\localconfig.vdf", """
            "UserLocalConfigStore" { "Software" { "Valve" { "Steam" { "apps" {
                "220" { "LastPlayed" "1750000000" "Playtime" "50" }
            } } } } }
            """);

        var reader = new SteamReader(fs);
        var merged = reader.ReadMergedPlayRecords(reader.ReadUsers(SteamRoot));

        Assert.Equal(1750000000, merged[220].LastPlayedUnix);
        Assert.Equal(100, merged[220].PlaytimeMinutes);
    }

    [Fact]
    public void アカウントID0のユーザーは無視する()
    {
        var fs = BuildFileSystem();
        fs.AddTextFile($@"{SteamRoot}\userdata\0\config\localconfig.vdf", """
            "UserLocalConfigStore" { }
            """);

        Assert.Empty(new SteamReader(fs).ReadUsers(SteamRoot));
    }

    [Fact]
    public void libraryfoldersが無くてもsteamapps単体で動く()
    {
        var fs = new FakeFileSystem();
        fs.AddSizedFile($@"{SteamRoot}\steamapps\appmanifest_1.acf", 10);

        var libraries = new SteamReader(fs).ReadLibraries(SteamRoot);

        Assert.Single(libraries);
        Assert.Equal(SteamRoot, libraries[0].Path);
    }
}
