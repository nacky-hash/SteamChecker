# ARCHITECTURE

```
src/
  SteamChecker.Core/          net10.0 / OS 非依存 / テスト対象の本体
    Steam/                      VDF パーサ、libraryfolders / appmanifest / localconfig
    Analysis/                   フォルダ走査、特徴検出、圧縮率推定、判定エンジン
    Compression/                compact.exe 実行、事前検査、操作ログ
    Presentation/               日本語の文言と配色（判定ロジックからは分離）
    IFileSystem.cs              ファイルシステム抽象
    PhysicalFileSystem.cs       実装（GetCompressedFileSizeW の P/Invoke を含む）
  SteamChecker.Cli/           net10.0 / コマンドライン版
  SteamChecker.App/           net10.0-windows / WPF 版。判定ロジックを持たない
tests/
  SteamChecker.Tests/         テスト本体（xUnit）
  SteamChecker.TestRunner/    NuGet が使えない環境用の依存ゼロランナー
```

## Core が守る境界

`Core` は Windows API に直接依存しない。理由と効果は `docs/DECISIONS.md` D-009。

- ファイルシステムアクセスは全て `IFileSystem` 経由
- レジストリ参照はアプリ層からデリゲートで注入
- 日時は `TimeProvider` を注入（テストで固定できる）
- プロセス一覧・ファイルロックの検査もアプリ層から注入（`PreFlightChecker`）

**WPF に判定ロジックを移さないこと。** UI は Core を呼んで結果を並べるだけにする。

配色（`Presentation/AdviceColors.cs`）も Core 側にある。WPF に置くと
「分類を増やしたのに色を足し忘れて黙って灰色になる」事故をテストで防げないため。

## 2 段階の読み取り（D-016）

処理時間が 3 桁違うので、UI からは別々に呼べるようにしてある。

| | 内容 | 実測 |
|---|---|---|
| `LibraryScanner.ReadTitles` | manifest と localconfig だけ読む。一覧の即時表示用 | 49〜388 ms |
| `LibraryScanner.Scan` | 全ゲームのファイルを読んで圧縮率を実測 | 約 240 秒 / 442GB |

## 判定の流れ（`Scan`）

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
AdviceColors        分類ごとの配色（同上）
```

## 書き込みの流れ（`compress` / `restore`）

```
PreFlightChecker    fail-closed の事前検査。判定できないものは実行しない（D-013）
  ↓                 ライブラリ配下か / manifest 現存 / NTFS / reparse point /
                    Steam・ゲーム起動中 / 容量測定 / 空き容量
CompactExeEngine    compact.exe を実行。出力はパースせず前後の実占有を自前で測る（D-004）
  ↓
OperationJournal    実行内容を JSON Lines で追記記録
  ↓
（UI）              実測の前後サイズで一覧の行をその場更新する。再スキャンは不要
```

## 出力の 6 分類

```
NTFS 以外 / DirectStorage 検出        → 圧縮非推奨
既に圧縮済み                          → 圧縮済み（実測の削減量を表示）
削減率 < 10% or 削減量 < 1GB          → 圧縮しても効果小
アンチチート検出                      → アンチチートあり（自己責任で）
30日以内に更新                        → 更新が多い（圧縮が解けやすい）
上記以外                              → 圧縮推奨
```

インストール未完了（ダウンロード中・更新中）は、上記より先に圧縮非推奨として弾く。

これとは独立に、長期未起動 × 大容量 × 圧縮が効かない、には
「削除すれば N GB 空きます」という事実を添える（D-007）。
