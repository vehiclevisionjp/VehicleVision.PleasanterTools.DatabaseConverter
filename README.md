# VehicleVision.PleasanterTools.DatabaseConverter

Pleasanter の DB を、**SQL Server・PostgreSQL・MySQL の間で相互に変換する**コマンドラインツールです。

Pleasanter 本体の CodeDefiner には `migrate`（DB 移行）があります。ただし移行元は SQL Server だけで、移行先は PostgreSQL と MySQL です。
このツールは、その先の「PostgreSQL → SQL Server」「MySQL → SQL Server」「PostgreSQL ⇄ MySQL」を含めて、対応する 3 つの DBMS の**あらゆる向き**を扱います。

> **開発中です。** まだ動作するものはありません。仕様は [アーキテクチャ方針](_documents/アーキテクチャ方針.md) にあります。

## 変換できる組み合わせ

| 変換元 \ 変換先 | SQL Server | PostgreSQL | MySQL |
| --- | --- | --- | --- |
| SQL Server | — | 対象 | 対象 |
| PostgreSQL | 対象 | — | 対象 |
| MySQL | 対象 | 対象 | — |

## 方針

- **変換元の DB は読み取りだけ。** 書き換えません
- **変換先の表の定義は、Pleasanter 本体（CodeDefiner）が作ったものに合わせます。** このツールは表を勝手に設計しません
- 件数と内容の整合を、変換のあとに確かめます
- Pleasanter 本体のコードは取り込みません（表と列の定義は、本体のソースを事実確認の参照として読んだ上で、このツールで実装しています）

## ライセンス

AGPL-3.0-or-later と商用のデュアルライセンスです。詳しくは [LICENSING.md](LICENSING.md) を、貢献は [CONTRIBUTING.md](CONTRIBUTING.md) を参照してください。
依存のライセンスは [NOTICE](NOTICE) にあります。開発環境と運用は [開発ガイド](_documents/開発ガイド.md) と [開発運用](_documents/開発運用.md) にあります。
