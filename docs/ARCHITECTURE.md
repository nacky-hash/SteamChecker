# ARCHITECTURE

```
src/
  SteamChecker.Core/          net10.0 / OS 非依存 / テスト対象の本体
    Steam/                      VDF パーサ、libraryfolders / appmanifest / localconfig
    Analysis/                   フォルダ走査、特徴検出、圧縮率推定、判定エンジン
    Compression/                compact.exe 実行、操作ログ
    Presentation/               日本語の文言（判定ロジックからは分離）
    IFileSystem.cs              ファイルシステム抽象
    PhysicalFileSystem.cs       実装（GetCompressedFileSizeW の P/Invoke を含む）
  SteamChecker.Cli/           net10.0 / コマンドライン版
tests/
  SteamChecker.Tests/         テスト本体（xUnit）
  SteamChecker.TestRunner/    NuGet が使えない環境用の依存ゼロランナー
```

## Core が守る境界

`Core` は Windows API に直接依存しない。理由と効果は `docs/DECISIONS.md` D-009。

- ファイルシステムアクセスは全て `IFileSystem` 経由
- レジストリ参照はアプリ層からデリゲートで注入
- 日時は `TimeProvider` を注入（テストで固定できる）

**WPF プロジェクトを追加する際、判定ロジックをそちらに移さないこと。**

## 判定の流れ

```
SteamLocator        Steam の場所を特定（レジストリ / 既定パス）
  ↓
LibraryFolders      libraryfolders.vdf から全ライブラリを列挙
  ↓
AppManifest         appmanifest_*.acf から appid / name / size / LastUpdated
LocalConfig         localconfig.vdf から LastPlayed（全ローカルユーザー分）
  ↓
FolderProfile       フォルダを走査し、論理/物理サイズと拡張子分布を得る
FeatureDetector     DirectStorage / アンチチート / 圧縮済み / ファイルシステム
SamplingEstimator   層化サンプリング → ChunkedBrotliProbe で圧縮率を推定
  ↓
Advisor             上記を突き合わせて 6 分類に振り分ける
  ↓
AdviceFormatter     日本語の文言に変換（判定ロジックとは分離）
```

## 出力の 6 分類

```
NTFS 以外 / DirectStorage 検出        → 圧縮非推奨
既に圧縮済み                          → 何もしない
削減率 < 10% or 削減量 < 1GB          → 圧縮しても効果小
アンチチート検出                      → 圧縮可（要 確認）
30日以内に更新                        → 圧縮可（要 自動再圧縮）
上記以外                              → 圧縮推奨
```

これとは独立に、長期未起動 × 大容量 × 圧縮が効かない、には
「削除すれば N GB 空きます」という事実を添える（D-007）。
