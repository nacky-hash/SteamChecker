# RELEASE_CHECKLIST

## Phase 0（読み取り専用）を出すまで

- [x] .NET SDK 10 を導入し、`dotnet build` / `dotnet test` が通ること（2026-07-30: 132件成功）
- [x] `dotnet publish -r win-x64 --self-contained` が通ること（CLI 70.2MB / App 125.5MB）
- [x] SmartScreen で何が出るか自分の環境で確認し、README に正直に書く
      （2026-07-30 実機確認: 「WindowsによってPCが保護されました / 不明な発行元」。README に画面文言と実行手順を記載）
- [x] ソース公開（MIT）→ https://github.com/nacky-hash/SteamChecker （2026-07-30）
- [x] リリース成果物の SHA-256 を公開 → v0.1.0-alpha Release に SHA256SUMS.txt を添付
- [x] 署名方針を明記する → README「配布の透明性」に無署名であることを明記
- [x] 通信ゼロであることを、コードのどこを見れば確認できるか README に書く
- [ ] スクリーンショットに実在のユーザー名・Steam ID・フルパスが写っていないこと（スクショ未作成）
- [x] 「必ず N% 削減」「性能低下なし」といった表現が一切ないこと（2026-07-30 レビューで確認・修正済み）

### 成果物の SHA-256（2026-08-01、部分解除の検出・アンチチート検証の後）

```
SteamChecker.App.exe                0.3 MB  A75958518CEF26BF86B44CF06D1AAD4361189826702363A43BE66C60894EA7BC
SteamChecker.App-selfcontained.exe 133.4 MB  F4BAF284FD40E1A3D7C702D596D46CBAB7F2DA97DFE9D70C04F813BF2AA70781
steamchecker.exe                   70.3 MB  9A0523A3B8E23571B6EE834F873C73DD4CD6D3724231819C48F40855BEA932F7
```

### リリース前に必ず通す検証（過去に 2 回落とし穴を踏んだ）

- [x] **隔離フォルダに exe 単体をコピーして起動する**（2026-09-12: App 2 種・CLI とも確認）
      （2026-07-30: ネイティブ DLL 非同梱で、publish フォルダ外では起動しない不良品を配った）
- [x] **一覧にデータが表示された状態まで確認する。ウィンドウが出ただけで合格にしない**
      （2026-09-12: rows=47 まで確認）
      （2026-07-31: グループ見出しの TwoWay バインドで、行が描画された瞬間にクラッシュした。
      前回はリストが空のまま「起動 OK」と判断したため見逃した）
- [x] `%LOCALAPPDATA%\SteamChecker\crash.log` が生成されていないこと（2026-09-12 確認）

自動化スクリプトは `tools/release_verify.ps1`（隔離起動 → 行数 → crash.log を一括確認）。

```powershell
powershell -ExecutionPolicy Bypass -File tools/release_verify.ps1 -ReleaseDir <publish先> -MinRows 1
```

各行の末尾に `PASS` が出れば合格。`rows=0` は**不合格**（ウィンドウだけ出て中身が無い状態）。

## Phase 1（圧縮実行）を出すまで

- [ ] Phase 0 の全項目
- [x] `docs/TEST_PLAN.md` の安全側の検査が全てテスト付きで実装済み（2026-07-30）
- [x] 中断・失敗時にゲームデータが破損しないことをテストで保証（`CompactExeEngineTests` キャンセル注入）
- [x] 操作ログにユーザー名・Steam ID・フルパスが残らないこと
      （※ Path フィールドにインストールパスは残る。カスタムライブラリがユーザー名を含む場合は写り込む — 既知の限界として明記）
- [ ] UAC 昇格のフォールバック（常時要求はしない）— 未実装（既定構成では不要と実測済み）
- [x] 実ゲームでの圧縮→起動確認を、再ダウンロード可能な小容量タイトルで実施
      （2026-07-30: Slots & Daggers 全工程成功・ハッシュ完全一致。1タイトルのみ）

## 配布判定

**配布不可 / 開発者限定テスト可 / 少人数アルファ可 / 一般ベータ可** のいずれかを、
根拠と残存リスクを添えて明示する。楽観的に判定しない。

### 成果物の SHA-256（2026-09-12、D-020〜D-022 の後）

```
steamchecker.exe                    70.3 MB  768347B6152DCB8A9BD98D3D1378BA2D986D900CFCC035B1E3FF8913DFABD067
SteamChecker.App.exe                 0.3 MB  7E9FA37E187FC772282EB5D3A9CDE14987EC95D5DA052C46C2C8F57EAEFEECEB
SteamChecker.App-selfcontained.exe 133.4 MB  2EEA712696AFC89FF3755F09C84716341B58C1F7E5C2B0075C9DE58D34EB2B79
```

再現ビルド:

```
dotnet publish src/SteamChecker.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
dotnet publish src/SteamChecker.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
dotnet publish src/SteamChecker.App -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true
```

### 隔離検証の結果（2026-09-12）

`tools/release_verify.ps1` を通した。**両方 PASS。**

| 成果物 | 起動 | ウィンドウ | 行数 | crash.log | 判定 |
|---|---:|---:|---:|---|---|
| SteamChecker.App.exe | 1,888 ms | 1 | 47 | なし | PASS |
| SteamChecker.App-selfcontained.exe | 2,234 ms | 1 | 47 | なし | PASS |

CLI は GUI が無いため別途確認した。隔離フォルダに `steamchecker.exe` 単体を
コピーして `--help` を実行し、正常終了（終了コード 0）することを確認。

**行数 47 まで見ている**のが要点。前回は 44 件だった（タイトルが増えたため）。
ウィンドウが出ただけで合格にせず、一覧が描画された状態を確認している。
