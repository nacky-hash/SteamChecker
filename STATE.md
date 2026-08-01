# STATE — 現在地・次の一手・再開手順

再開するときは「**再開して**」の一言でよい。このファイルから復帰する。

最終更新: 2026-08-01

---

## 現在地（一言で）

**公開済み。知人に配れる状態。未コミット・未 push はゼロ。**

- リポジトリ: https://github.com/nacky-hash/SteamChecker （Public / MIT）
- リリース: v0.1.0-alpha（Pre-release）
- 最新コミット: `23cc2e6`（origin/main と一致）
- テスト: 149 件、両ランナー（xUnit / 依存ゼロ TestRunner）で全て成功
- このセッションの ID: `a2cc48c6-839c-4b16-a3da-cf0605498573`

## ユーザーの環境で実施済み

| | |
|---|---|
| 圧縮したタイトル | theHunter / Sniper Elite 5 / Jotunnslayer / Shape of Dreams（計 78.2 GB 削減） |
| 削除したタイトル | Grand Theft Auto V Enhanced（96 GB） |
| C: の空き | 479 GB → 608 GB |
| 動作確認 | Sniper Elite 5（EasyAntiCheat）を圧縮したまま起動・プレイして正常。映像音声とも問題なし |

Slots & Daggers は検証で圧縮→復元したので**元の状態**（219 MB、未圧縮）。

## 次の一手（優先順）

1. **知人からの反応待ち。** 「動かない」と言われたら
   `%LOCALAPPDATA%\SteamChecker\crash.log` を送ってもらう。
   「見込みと実際が違う」なら ±6pt までは想定内（`docs/RESEARCH.md` §6）
2. **残っている未確認**（急がない）
   - BattlEye 系のアンチチートは未確認（EasyAntiCheat のみ n=1 で確認済み）
   - 複数ライブラリ（D: や外付け）を持つ環境での動作は未確認
   - スクリーンショットを README に未掲載（実名・パスの写り込みに注意）
3. **やらないと決めたこと**（蒸し返さない）
   - 常駐監視・自動再圧縮（D-018）。容量を空けるツールが常駐するのは本末転倒
   - ReadyToRun（D-017）。実測したら起動が遅くなった

## 再開手順

```powershell
cd C:\Users\nakan\dev\SteamChecker
git log --oneline -3          # 23cc2e6 が最新なら、この STATE.md は最新
dotnet test -c Release        # 149 件success を確認
```

セッションが CCD の一覧から消えていても履歴は無事
（メモリ `ccd-missing-session-recovery` 参照。該当フォルダで PowerShell を開けば戻る）。

## 読む順番

| ファイル | 内容 |
|---|---|
| `AGENTS.md` | ルールの正本 |
| `docs/STATUS.md` | 作業記録。末尾が最新 |
| `docs/DECISIONS.md` | 設計判断 D-001〜D-019。**特に D-016〜D-019 が今回の分** |
| `docs/RESEARCH.md` | 実測データ。再測定不要 |
| `docs/RELEASE_CHECKLIST.md` | 配布前に必ず通す検証（隔離起動・行表示・crash.log なし） |

## このプロジェクトで繰り返した失敗（同じ轍を踏まないこと）

いずれも「確認したつもりで、肝心の部分を見ていなかった」もの。

1. **空のリストで「起動 OK」と判断** → グループ描画時のクラッシュを見逃した
2. **publish フォルダ内でしか起動確認せず** → exe 単体では起動しない不良品を配った
3. **ランチャープロセスの生存を「動作」と誤認** → ゲーム本体は起動していなかった
4. **「効果小」の色がフォールバック色と同一** → 故障しても故障に見えない状態だった

対策として `docs/RELEASE_CHECKLIST.md` に必須検証項目を、
`tools/release_verify.ps1` に自動化スクリプトを置いてある
（隔離フォルダへ exe 単体をコピー → 起動 → 行数 → crash.log を一括確認）。

配布前は必ずこれを通すこと:

```powershell
# publish 後に実行。windows=1 / rows=44 / no crash が全部揃えば合格
powershell -ExecutionPolicy Bypass -File tools\release_verify.ps1
```
