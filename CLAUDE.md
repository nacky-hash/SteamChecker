# CLAUDE.md

**プロジェクトルールの正本は [AGENTS.md](AGENTS.md)。矛盾した場合は AGENTS.md を優先すること。**

## 着手前に読むもの

```
AGENTS.md              ルール（正本）
docs/STATUS.md         現在地。何が終わっていて何が残っているか
docs/DECISIONS.md      設計判断とその根拠
docs/RESEARCH.md       Windows 実機での実測データ（再測定不要）
docs/ARCHITECTURE.md   構成
docs/TEST_PLAN.md      テスト方針
```

## よく使うコマンド

```bash
# ビルド
dotnet build src/SteamChecker.Cli/SteamChecker.Cli.csproj

# テスト（NuGet あり）
dotnet test

# テスト（NuGet に到達できない環境用の依存ゼロランナー）
dotnet run --project tests/SteamChecker.TestRunner

# 実行
dotnet run --project src/SteamChecker.Cli -- scan
```

## 落とし穴

- `compact.exe` の標準出力をパースしない。日本語環境で壊れる（`docs/RESEARCH.md` 参照）
- WOF 圧縮ファイルに `ReparsePoint` 属性は立たない。属性で圧縮済み判定をしない
- 圧縮率の推定は 32 KiB チャンク単位で行う。ファイル全体で測ると 1 割過大に出る
