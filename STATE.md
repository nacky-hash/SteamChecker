# STATE — 現在地・次の一手・再開手順

再開するときは「**再開して**」の一言でよい。このファイルから復帰する。

最終更新: 2026-09-12

---

## 現在地（一言で）

**公開済み。知人に配れる状態。**
**2026-09-11 に 6 週間ぶりの再チェックを実施し、6 タイトル / 28.1 GB を追加削減。**
**2026-09-12 に D-020（落とされる前に止まる / 落とされたら次回に伝える）と
D-021（圧縮の到達点は記録から引く）を実装。**

- リポジトリ: https://github.com/nacky-hash/SteamChecker （Public / MIT）
- リリース: **v0.2.0-alpha**（Pre-release / 2026-09-12 公開。D-020〜D-022 を反映）
  - 成果物 3 点＋SHA256SUMS.txt を添付。隔離検証は 3 点とも PASS（rows=47 / crash なし）
  - 前: v0.1.0-alpha（2026-07-30）
- 最新コミット: `23cc2e6`（origin/main と一致）
- テスト: **163 件**、両ランナー（xUnit / 依存ゼロ TestRunner）で全て成功
- 直近のセッション ID: `9ee94bcc-7af3-4607-9134-21fabca8ad8e`（2026-09-11 / 再チェック）
- その前: `a2cc48c6-839c-4b16-a3da-cf0605498573`（2026-07-30〜31 / 公開まで）

## ユーザーの環境で実施済み

| | |
|---|---|
| 圧縮したタイトル（7/30〜31） | theHunter / Sniper Elite 5 / Jotunnslayer / Shape of Dreams（計 78.2 GB 削減） |
| 圧縮したタイトル（9/12） | Medieval Crafter: Blacksmith（1.10 GB。8/8 の更新で解けた分を回収） |
| 圧縮したタイトル（9/11） | Sniper Elite: Resistance / R.I.P. / The Spell Brigade / Yet Another Zombie Survivors / Black Jacket / Monsters are Coming!（計 28.1 GB 削減） |
| 削除したタイトル | Grand Theft Auto V Enhanced（96 GB） |
| C: の空き | 479 → 608 GB（8/1） / 445 → 465 GB（9/11。6 週間で 163 GB 減っていた分を一部回収） |
| 動作確認 | Sniper Elite 5（EasyAntiCheat）を圧縮したまま起動・プレイして正常。映像音声とも問題なし |

Slots & Daggers は検証で圧縮→復元したので**元の状態**（219 MB、未圧縮）。

## 次の一手（優先順）

1. **知人からの反応待ち。** 「動かない」と言われたら
   `%LOCALAPPDATA%\SteamChecker\crash.log` を送ってもらう。
   「見込みと実際が違う」なら数 pt は想定内
   （実圧縮 16 タイトルで平均 2.2pt / 最大 4.9pt。`docs/RESEARCH.md`）。
   **v0.2.0-alpha を案内すること**（v0.1.0-alpha には中断時の通知が無い）
2. **残っている未確認**（急がない）
   - BattlEye 系のアンチチートは未確認（EasyAntiCheat のみ n=1 で確認済み）
   - 複数ライブラリ（D: や外付け）を持つ環境での動作は未確認
   - スクリーンショットを README に未掲載（実名・パスの写り込みに注意）
   - **GUI でのメモリ監視の発火**。WPF には閾値を変える経路が無く、
     実際に 1 GiB を割らないと発火しないため未確認
     （中断通知のダイアログは 2026-09-12 に実機確認済み）
   - **本物のメモリ枯渇での自主中断**。閾値 1 TB での人為的な発火は確認済みだが、
     実際にメモリが枯渇していく状況で間に合うかは未検証
   - **初めて圧縮するタイトルでは推定の弱点が残る**。D-021 は過去の実測が
     ある場合の回避策で、サンプリング推定そのものは直していない
     （R.I.P. で約 1.8 GB 一貫して過大。原因未特定。`docs/RESEARCH.md`）
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
5. **`git status` が clean なのを「bin/ も最新」と読んだ**（2026-09-12 に判明）
   → D-019 を含まない 2 日前の exe で実機測定し、結論を 1 つ書き損じた。
   実機で測る前に、使う exe のビルド日時とコミット日時を照合すること

対策として `docs/RELEASE_CHECKLIST.md` に必須検証項目を、
`tools/release_verify.ps1` に自動化スクリプトを置いてある
（隔離フォルダへ exe 単体をコピー → 起動 → 行数 → crash.log を一括確認）。

配布前は必ずこれを通すこと:

```powershell
# publish 後に実行。各行の末尾に PASS が出れば合格（rows=0 は不合格）
powershell -ExecutionPolicy Bypass -File tools/release_verify.ps1 -ReleaseDir <publish先> -MinRows 1
```
