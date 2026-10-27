# _reference（参考専用・AGPL）

Pleasanter 本体（**GNU AGPL v3**）は、AI（Claude・Copilot・Codex）が
API 仕様・DB スキーマ・列定義・挙動を事実確認するためだけに読む参照ソース。
本リポジトリでは追跡せず、必要なときに上流の `main` の先頭を取得する。
CI は参照ソースを使わず、checkout の `submodules: false` も維持する。

- 上流: <https://github.com/Implem/Implem.Pleasanter>
- 既定の取得先: `_reference/Implem.Pleasanter/`（Git 管理外）
- 共有取得先: 環境変数 `PLEASANTER_REFERENCE_DIR`（その場所を直接読む）
- この `README.md` は引き続き Git で追跡する

## 厳守事項

- **コードをコピー・流用・リンク・プロジェクト参照しない。**
  本アプリは本体を改造せず、変換元・変換先の DB の情報スキーマから独立して変換する
- **本製品はデュアルライセンス（AGPL ＋ 商用）。**
  本体の著作権は自社に無く、取り込むと商用側を提供できなくなる
- ビルド対象・配布物・ソリューションへ含めない
- 取得した本体の作業ツリーを編集しない。本リポジトリへのコミット対象でもない
- 実機未検証の結果を動作確認済みと書かない。DB の照合順序・権限・日時変換などは実機で確認する

「どうせ AGPL 側があるのだから取り込んでよい」ではない。
このツールは標準 API を叩くだけのアプリとは異なり DB を直接扱うが、本体コードは持ち込まない。

## 取得と更新

事実確認を始める前に毎回実行する。Git と bash が必要で、Windows では Git Bash を使う。

```bash
bash scripts/sync-reference.sh
```

Windows の深い worktree で長いパスが原因の取得失敗になる場合は、
次の指定で Git の長いパス対応をその実行中だけ有効にできる（Git 設定は保存しない）。

```bash
GIT_CONFIG_COUNT=1 GIT_CONFIG_KEY_0=core.longpaths GIT_CONFIG_VALUE_0=true bash scripts/sync-reference.sh
```

worktree 間で共有するときは、PowerShell から次のように指定できる。

```powershell
$env:PLEASANTER_REFERENCE_DIR = 'D:\references\Implem.Pleasanter'
bash scripts/sync-reference.sh
```

未取得なら `main` を `git clone --depth 1` で浅く取得する。
既存なら `git fetch --depth 1 origin main` と `git checkout --detach FETCH_HEAD` で更新する。
終了時に Path・短い SHA・コミット日時・Status に加え、完全な FullSHA・取得日時（UTC）・
csproj の Version を出す。Version を読めないときは警告と `Version=unknown` を出し、推測しない。

変更済み・ステージ済み・未追跡ファイルがある作業ツリー、異なる origin、
別リポジトリや非空の取得先では停止する。通信失敗は失敗コードで終了し、既存の HEAD とファイルを維持する。
新規 clone は一時取得先で成功してから配置する。並行取得はロックで止める。
異常終了で `.sync-lock` が残った場合は、同期が実行中でないことを確認してから空ディレクトリを取り除く。

`PLEASANTER_REFERENCE_DIR` 指定時は案内だけを出し、ローカルへのジャンクションやシンボリックリンクを作らない。
相対パスはリポジトリ直下を基準にする。共有先を他の AI が読んでいる間は更新しない。
本アプリには `references.json` を置かず、本体の `main` 1 件だけを取得する。

## 確認対象と限界

| 対象 | 確認できること |
| --- | --- |
| `Implem.CodeDefiner/Starter.cs` | `migrate` が許可する DBMS の向き |
| `Implem.CodeDefiner/Functions/AspNetMvc/CSharp/Migrator.cs` | 標準の移行処理 |
| `Implem.Pleasanter/App_Data/Definitions` | 列定義・採番・SQL 定義 |
| `Implem.Pleasanter/Controllers/Api/ItemsController.cs` | 標準 API のエンドポイント |
| `Implem.Pleasanter/Libraries/Requests/Context.cs` | API キーの受け渡し方式 |

実際の DB の照合順序・権限・接続設定、API 応答の日時書式などはソース確認だけでは保証しない。
[アーキテクチャ方針](../_documents/アーキテクチャ方針.md) に過去のソース・実機確認の記録を残している。

## 事実の記録と再現性

2026-10-26 の Template の決定に従い、サブモジュールと日次更新 workflow を廃止する。
参照用のポインタ更新 PR や bot の迂回権限は不要。既存のブランチ・Draft PR・Issue の整理は別作業とする。
再現性は gitlink ではなく、文書に残す**確認した完全な SHA・ソースへの固定リンク・参照日**で担保する。
過去の記録を未確認の最新 SHA へ自動で置き換えない。
`main` はリリース前の変更を含むため、Version だけでリリース済みと判断せず、
必要なら上流の `Pleasanter_*` タグとの対応を確認する。

移植元は Template の SHA `3312f003fbe9827090e20bc643800824bdf30e22`（2026-10-27 参照）。

- [承認済みの配布手順][procedure]
- [決定: vehiclevisionjp/VehicleVision.PleasanterTools.Template#61][decision]
- [決定: vehiclevisionjp/VehicleVision.PleasanterTools.Template#63][followup]
- [git clone](https://git-scm.com/docs/git-clone)（浅い clone と branch 指定、2026-10-27 参照）
- [git fetch](https://git-scm.com/docs/git-fetch)（`--depth` と `FETCH_HEAD`、2026-10-27 参照）

[procedure]: https://github.com/vehiclevisionjp/VehicleVision.PleasanterTools.Template/blob/3312f003fbe9827090e20bc643800824bdf30e22/_documents/参照ソースのオンデマンド取得への配布手順.md
[decision]: https://github.com/vehiclevisionjp/VehicleVision.PleasanterTools.Template/issues/61
[followup]: https://github.com/vehiclevisionjp/VehicleVision.PleasanterTools.Template/issues/63
