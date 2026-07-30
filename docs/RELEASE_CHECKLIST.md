# RELEASE_CHECKLIST

## Phase 0（読み取り専用）を出すまで

- [x] .NET SDK 10 を導入し、`dotnet build` / `dotnet test` が通ること（2026-07-30: 132件成功）
- [x] `dotnet publish -r win-x64 --self-contained` が通ること（CLI 70.2MB / App 125.5MB）
- [ ] SmartScreen で何が出るか自分の環境で確認し、README に正直に書く
      （Release からダウンロードして確認可能になった。実行はユーザーの目視確認待ち）
- [x] ソース公開（MIT）→ https://github.com/nacky-hash/SteamChecker （2026-07-30）
- [x] リリース成果物の SHA-256 を公開 → v0.1.0-alpha Release に SHA256SUMS.txt を添付
- [x] 署名方針を明記する → README「配布の透明性」に無署名であることを明記
- [x] 通信ゼロであることを、コードのどこを見れば確認できるか README に書く
- [ ] スクリーンショットに実在のユーザー名・Steam ID・フルパスが写っていないこと（スクショ未作成）
- [x] 「必ず N% 削減」「性能低下なし」といった表現が一切ないこと（2026-07-30 レビューで確認・修正済み）

### 成果物の SHA-256（2026-07-30 ビルド、GUI 圧縮配線後）

```
steamchecker.exe      70.2 MB  5C5E218B9C191FD3A1EA1B992ADE1B6CEEBAB27DAC63CC6EA8CCDC9E4FC59C09
SteamChecker.App.exe 125.5 MB  DD1028C2EB4B7CFB1E472FDE8355BC74E842CFFC00B0A9D1FE34D1DD4D32EFF4
```

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
