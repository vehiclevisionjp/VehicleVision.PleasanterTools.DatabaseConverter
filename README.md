# VehicleVision.PleasanterTools.DatabaseConverter

Pleasanter の DB を、**SQL Server・PostgreSQL・MySQL の間で相互に変換する**コマンドラインツールです。

Pleasanter 本体の CodeDefiner には `migrate`（DB 移行）があります。ただし移行元は SQL Server だけで、移行先は PostgreSQL と MySQL です。
このツールは、その先の「PostgreSQL → SQL Server」「MySQL → SQL Server」「PostgreSQL ⇄ MySQL」を含めて、対応する 3 つの DBMS の**あらゆる向き**（6 通り）を扱います。

> **開発中です（0.x）。** 実データでの運用の前に、必ず[ドライラン](#ドライラン)と、バックアップからの試し変換を行ってください。
> 仕様と未決定事項は [アーキテクチャ方針](_documents/アーキテクチャ方針.md) にあります。

## 安全上の注意

1. **まず `--dry-run`**（何を何行移すか、警告や定義の不一致を出すだけ。何も書きません）
2. **変換元と変換先の両方をバックアップ**してから実行する。変換元は止めて（Pleasanter を停止して）から変換する
3. **変換先は、Pleasanter 本体（CodeDefiner）が作った表の揃った空の DB** にする。このツールは表を作りません
   （作るのは完了の記録用の管理表 `__DatabaseConverterProgress` だけです）
4. **変換元は読み取りだけ**です（書き換えません）。SQL Server の変換元には、読み取り専用の権限の利用者を使うことを勧めます
5. 変換先の Pleasanter は、同じバージョンの CodeDefiner で作った表にしてください（列は同名のものだけを突き合わせます）
6. 接続文字列・パスワードを**引数へ直接書くことはできません**（環境変数・ファイル・標準入力）。ログにも出しません

## 導入

.NET 10 ランタイムが必要です（実行にはランタイムだけで足りますが、`dotnet tool install` には .NET 10 SDK が要ります）。次のどちらかで導入できます。

- **dotnet tool（nuget.org）**: `dotnet tool install -g VehicleVision.PleasanterTools.DatabaseConverter` で入れ、`database-converter` コマンドで実行します。更新は `dotnet tool update -g VehicleVision.PleasanterTools.DatabaseConverter` です
- **配布 ZIP（GitHub Releases）**: `VehicleVision.PleasanterTools.DatabaseConverter-<版>-portable.zip` を展開します

以下の例の `VehicleVision.PleasanterTools.DatabaseConverter` は、dotnet tool では `database-converter` に読み替えてください。

## 使い方

```text
VehicleVision.PleasanterTools.DatabaseConverter convert --from <dbms> --to <dbms> [オプション]
```

実行ファイル（`dotnet VehicleVision.PleasanterTools.DatabaseConverter.dll` でも実行できます。.NET 10 ランタイムが要ります）と、
`--from` / `--to` の値は `sqlserver` / `postgresql` / `mysql` です。**同じ DBMS 同士は引数エラー**です。

```bash
# 接続文字列は環境変数で渡す（既定の名前）
export DBCONV_SOURCE_CONNECTION='Server=old-db;Database=Implem.Pleasanter;User Id=reader;Password=...;TrustServerCertificate=True'
export DBCONV_TARGET_CONNECTION='Host=new-db;Database=Implem.Pleasanter;Search Path='"'"'"Implem.Pleasanter"'"'"';Username=owner;Password=...'

# 1. ドライラン
VehicleVision.PleasanterTools.DatabaseConverter convert --from sqlserver --to postgresql --dry-run
# 2. 変換
VehicleVision.PleasanterTools.DatabaseConverter convert --from sqlserver --to postgresql
# 3. 途中で止まったら、同じコマンドに --resume を付けて再実行
VehicleVision.PleasanterTools.DatabaseConverter convert --from sqlserver --to postgresql --resume
```

### オプション

| オプション | 内容 |
| --- | --- |
| `--from <dbms>` / `--to <dbms>` | 変換元と変換先の DBMS（必須） |
| `--source-connection-env <名前>` / `--target-connection-env <名前>` | 接続文字列を入れた環境変数の名前（既定は `DBCONV_SOURCE_CONNECTION` / `DBCONV_TARGET_CONNECTION`） |
| `--source-connection-file <パス>` / `--target-connection-file <パス>` | 接続文字列を書いたファイル（最初の空でない行）。権限を絞って置くこと |
| `--source-connection-stdin` / `--target-connection-stdin` | 標準入力から 1 行読む（どちらか一方だけ） |
| `--dry-run` | 何も書かず、何をどれだけ移すかだけ出す |
| `--resume` | 完了を記録した表をとばし、途中までの表を最初から入れ直す |
| `--tables A,B` / `--exclude-tables A,B` | 変換する表を絞る / 変換しない表を指定する |
| `--batch-size N` | バッチの行数（既定 1000、1〜100000） |
| `--max-batch-bytes N` | バッチを概算でこのバイト数でも区切る（既定 4194304）。`Binaries` などの大きな列の表で行数が減る |
| `--command-timeout 秒` | 1 つの SQL のタイムアウト（既定 600。0 は無制限） |
| `--disable-constraints` | 変換先の外部キーを、変換の間だけ無効にする（外部キーが循環する表があるとき） |
| `--cleanup-progress` | 全表が成功したら、管理表 `__DatabaseConverterProgress` を消す |
| `--help` / `-h`、`--version` | 使い方、版 |

終了コード: **全件成功 0、失敗があれば 1、引数エラー 2**。進み具合と結果（表ごとの件数・とばした数・丸めた数・失敗の一覧）は英語で出します。

### 接続文字列

接続文字列は各ドライバ（Microsoft.Data.SqlClient / Npgsql / MySqlConnector）の書式です。

- SQL Server: `Server=ホスト,ポート;Database=...;User Id=...;Password=...;TrustServerCertificate=True`（既定のスキーマ、通常 `dbo` の表を読み書きします）
- PostgreSQL: `Host=...;Port=...;Database=...;Username=...;Password=...;Search Path=...`。
  **Pleasanter の表はサービス名のスキーマ**（例: `Implem.Pleasanter`）にあるので、`Search Path` が要ります。
  サービス名に `.` を含むときは引用符つきで `Search Path='"Implem.Pleasanter"'` と書きます（表が見つからないときは、このことを案内して失敗します）
- MySQL: `Server=...;Port=...;Database=...;User=...;Password=...`（文字集合は `utf8mb4` で接続します）

### ドライラン

`--dry-run` は、表ごとに「コピーする行数・列」「完了済みでとばす」「途中の行を消して入れ直す」と、警告・定義の不一致を出します。何も書かず、管理表も作りません。
問題があれば終了コードは 1 です。

### 途中で止めたとき（再開の方式）

- 変換先に管理表 `__DatabaseConverterProgress` を作り、**表ごとに、コピーと整合の確認がすべて成功したときだけ**完了を記録します
- 既定では、**変換先の表が空でなければ、何も書かずに止まります**（安全側）。同じコマンドを 2 回流しても、データは重複しません
- `--resume` を付けると、完了済みの表はとばし（件数だけ確かめます）、記録の無い表に行が残っていれば**全部消して最初から**入れ直します
- 失敗（変換できない値など）のある表は完了を記録しないので、直してから `--resume` で、その表だけをやり直せます
- Ctrl+C は、実行中のバッチを終えて止まります（途中の表は `--resume` で入れ直せます）

## 何を確かめるか

- **件数と内容**: 変換のあと、変換先から読み戻した内容を、書いた値と突き合わせます（件数・主キーの集合・列ごとと行ごとのチェックサム）。確かめられなければ失敗として数えます
- **自動採番**: 変換先の続き番号を、変換元の次の番号（と変換先の最大値 + 1 の大きい方）へ合わせます（SQL Server の IDENTITY、PostgreSQL の sequence、MySQL の AUTO_INCREMENT）
- **変換できない値**（範囲外の日時、長すぎる文字列、PostgreSQL へ入れられない NUL 文字、大文字小文字・アクセントの違いで一意制約が破れる行など）は、**表・行・列を示して失敗として数え、その行だけをとばします**（黙って丸めません）。
  日時の小数秒や単精度への丸めは、丸めた数を表ごとに出します
- 型の変換表と、外部キー・採番・再開の詳細は [アーキテクチャ方針](_documents/アーキテクチャ方針.md) を読んでください

## 方針

- **変換元の DB は読み取りだけ。** 書き換えません
- **変換先の表の定義は、Pleasanter 本体（CodeDefiner）が作ったものに合わせます。** このツールは表を勝手に設計せず、変換先と変換元の実際の列を読んで、同名の列を突き合わせます
- Pleasanter 本体のコードは取り込みません（本体のソースは、挙動の事実確認の参照としてだけ読んでいます）

## ライセンス

AGPL-3.0-or-later と商用のデュアルライセンスです。詳しくは [LICENSING.md](LICENSING.md) を、貢献は [CONTRIBUTING.md](CONTRIBUTING.md) を参照してください。
依存のライセンスは [NOTICE](NOTICE) にあります（Windows の SQL Server 接続が使う `Microsoft.Data.SqlClient.SNI.runtime` だけは、独自の許諾で、再頒布の条件を NOTICE に書いています）。
開発環境と運用は [開発ガイド](_documents/開発ガイド.md) と [開発運用](_documents/開発運用.md) にあります。
