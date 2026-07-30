using SteamChecker.Core.Steam;

namespace SteamChecker.Tests;

public class VdfParserTests
{
    [Fact]
    public void 単純なキーと値を読める()
    {
        var vdf = """
                  "AppState"
                  {
                      "appid"     "1091500"
                      "name"      "Cyberpunk 2077"
                  }
                  """;

        var root = VdfParser.Parse(vdf);

        Assert.Equal("1091500", root.GetString("AppState", "appid"));
        Assert.Equal("Cyberpunk 2077", root.GetString("AppState", "name"));
    }

    [Fact]
    public void キーの大文字小文字を区別しない()
    {
        var root = VdfParser.Parse("""
                                   "AppState" { "LastUpdated" "1700000000" }
                                   """);

        Assert.Equal("1700000000", root.GetString("appstate", "lastupdated"));
    }

    [Fact]
    public void ネストを辿れる()
    {
        var vdf = """
                  "UserLocalConfigStore"
                  {
                      "Software"
                      {
                          "Valve"
                          {
                              "Steam"
                              {
                                  "apps"
                                  {
                                      "620"
                                      {
                                          "LastPlayed"  "1600000000"
                                          "Playtime"    "512"
                                      }
                                  }
                              }
                          }
                      }
                  }
                  """;

        var apps = VdfParser.Parse(vdf)
            .Find("UserLocalConfigStore", "Software", "Valve", "Steam", "apps");

        Assert.NotNull(apps);
        Assert.Equal("1600000000", apps!.GetString("620", "LastPlayed"));
    }

    [Fact]
    public void エスケープを解釈する()
    {
        var root = VdfParser.Parse("""
                                   "libraryfolders" { "1" { "path" "D:\\SteamLibrary" } }
                                   """);

        Assert.Equal(@"D:\SteamLibrary", root.GetString("libraryfolders", "1", "path"));
    }

    [Fact]
    public void 行コメントを無視する()
    {
        var vdf = """
                  // 先頭コメント
                  "AppState"
                  {
                      // 途中のコメント
                      "appid" "440"   // 行末コメント
                  }
                  """;

        Assert.Equal("440", VdfParser.Parse(vdf).GetString("AppState", "appid"));
    }

    [Fact]
    public void 条件付きトークンを読み飛ばす()
    {
        var vdf = """
                  "root"
                  {
                      "key"  "value"  [$WIN32]
                      "next" "second"
                  }
                  """;

        var root = VdfParser.Parse(vdf);

        Assert.Equal("value", root.GetString("root", "key"));
        Assert.Equal("second", root.GetString("root", "next"));
    }

    [Fact]
    public void 引用符なしトークンを読める()
    {
        var root = VdfParser.Parse("root { appid 440 }");

        Assert.Equal("440", root.GetString("root", "appid"));
    }

    [Fact]
    public void 閉じ括弧が欠けていても読めたところまで返す()
    {
        // 電源断などで途中まで書かれた VDF を想定。例外で全体が死なないこと
        var root = VdfParser.Parse("""
                                   "AppState"
                                   {
                                       "appid" "220"
                                       "name"  "Half-Life 2"
                                   """);

        Assert.Equal("220", root.GetString("AppState", "appid"));
    }

    [Fact]
    public void 数値キーの子だけを列挙できる()
    {
        var vdf = """
                  "libraryfolders"
                  {
                      "contentstatsid" "-1234567890"
                      "0" { "path" "C:\\Steam" }
                      "1" { "path" "D:\\SteamLibrary" }
                  }
                  """;

        var children = VdfParser.Parse(vdf).Find("libraryfolders")!.NumericChildren().ToList();

        // contentstatsid は数値だが "-1234567890" もパースできてしまうため、
        // キー名側が数値であることを確認する
        Assert.Contains(children, c => c.Key == 0);
        Assert.Contains(children, c => c.Key == 1);
        Assert.Equal(2, children.Count(c => c.Key is 0 or 1));
    }

    [Fact]
    public void 深すぎるネストで例外を投げる()
    {
        var deep = string.Concat(Enumerable.Repeat("\"a\" {", 100))
                   + string.Concat(Enumerable.Repeat("}", 100));

        Assert.Throws<VdfParseException>(() => VdfParser.Parse(deep));
    }

    [Fact]
    public void 同名のオブジェクトキーはマージされる()
    {
        var vdf = """
                  "root"
                  {
                      "apps" { "1" "one" }
                      "apps" { "2" "two" }
                  }
                  """;

        var apps = VdfParser.Parse(vdf).Find("root", "apps")!;

        Assert.Equal("one", apps.GetString("1"));
        Assert.Equal("two", apps.GetString("2"));
    }
}
