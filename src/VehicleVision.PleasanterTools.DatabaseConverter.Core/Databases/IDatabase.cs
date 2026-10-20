using VehicleVision.PleasanterTools.DatabaseConverter.Core.Planning;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Schema;

namespace VehicleVision.PleasanterTools.DatabaseConverter.Core.Databases;

/// <summary>
/// 変換元の DB。**読み取りだけ**。変換の本体はこのインターフェースにだけ依存する（テストでは偽物に差し替える）。
/// </summary>
public interface ISourceDatabase : IAsyncDisposable
{
    Dbms Dbms { get; }

    /// <summary>現在のスキーマの表と列を、情報スキーマから読む。</summary>
    Task<SchemaInfo> ReadSchemaAsync(CancellationToken cancellationToken);

    /// <summary>表の行数。</summary>
    Task<long> CountRowsAsync(string table, CancellationToken cancellationToken);

    /// <summary>
    /// 表の行を、列の指定順で 1 行ずつ流す（全部をメモリへ載せない）。
    /// 値はドライバが返した .NET の値（NULL は <see cref="DBNull"/>）。
    /// </summary>
    IAsyncEnumerable<object?[]> ReadRowsAsync(
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> orderBy,
        CancellationToken cancellationToken);

    /// <summary>
    /// 自動採番列の「次に払い出される値」を返す。読めなければ <see langword="null"/>。
    /// SQL Server は IDENTITY の現在値、PostgreSQL は sequence、MySQL は AUTO_INCREMENT。
    /// </summary>
    Task<long?> ReadNextAutoIncrementAsync(string table, string column, CancellationToken cancellationToken);
}

/// <summary>1 回の書き込みの結果。</summary>
/// <param name="Inserted">入った行数。</param>
/// <param name="Failures">入らなかった行（バッチ内の添字と理由）。</param>
public sealed record WriteBatchResult(int Inserted, IReadOnlyList<RowWriteFailure> Failures);

/// <summary>入らなかった行。</summary>
/// <param name="RowIndex">バッチ内の添字（0 始まり）。</param>
/// <param name="Message">理由（DB のエラーメッセージ。英語とは限らない）。</param>
public sealed record RowWriteFailure(int RowIndex, string Message);

/// <summary>制約を一時的に無効にした状態。</summary>
public interface IConstraintScope
{
    /// <summary>無効にできなかったときの注意（英語）。無ければ <see langword="null"/>。</summary>
    string? Warning { get; }

    /// <summary>制約を元に戻す。戻すときの検査で見つかった問題（英語）を返す。</summary>
    Task<IReadOnlyList<string>> RestoreAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 変換先の DB。Pleasanter 本体（CodeDefiner）が作った表の揃った空の DB を前提にする。
/// このツールが作る表は、完了の記録用の管理表だけ。
/// </summary>
public interface ITargetDatabase : ISourceDatabase
{
    /// <summary>管理表を作る（無ければ）。</summary>
    Task EnsureProgressTableAsync(CancellationToken cancellationToken);

    /// <summary>完了を記録した表名。管理表が無ければ空。何も書かない。</summary>
    Task<IReadOnlySet<string>> ReadCompletedTablesAsync(CancellationToken cancellationToken);

    /// <summary>表の完了を記録する。</summary>
    Task MarkCompletedAsync(string table, long rows, CancellationToken cancellationToken);

    /// <summary>管理表を消す。</summary>
    Task DropProgressTableAsync(CancellationToken cancellationToken);

    /// <summary>表の行を全部消す（再開時に、途中までの表を入れ直すため）。</summary>
    Task ClearTableAsync(string table, CancellationToken cancellationToken);

    /// <summary>外部キーなどの制約を、変換が終わるまで一時的に無効にする。</summary>
    Task<IConstraintScope> DisableConstraintsAsync(IReadOnlyList<string> tables, CancellationToken cancellationToken);

    /// <summary>表への書き込みを始める（SQL Server の <c>SET IDENTITY_INSERT ON</c> など）。</summary>
    Task BeginTableAsync(TablePlan table, CancellationToken cancellationToken);

    /// <summary>表への書き込みを終える（<c>SET IDENTITY_INSERT OFF</c> など）。</summary>
    Task EndTableAsync(TablePlan table, CancellationToken cancellationToken);

    /// <summary>
    /// 行を書く。<paramref name="rows"/> の各行は <see cref="TablePlan.Mappings"/> の変換先の列の順。
    /// バッチ全体を 1 つのトランザクションで書き、失敗したら取り消して 1 行ずつ書き直し、入らなかった行を返す。
    /// </summary>
    Task<WriteBatchResult> WriteBatchAsync(TablePlan table, IReadOnlyList<object?[]> rows, CancellationToken cancellationToken);

    /// <summary>
    /// 自動採番の続き番号を、<c>max(変換元の次の番号, 変換先の最大値 + 1)</c> に合わせる。
    /// </summary>
    /// <returns>合わせた「次の番号」。合わせる必要が無い・できなかったときは <see langword="null"/>。</returns>
    Task<long?> AdjustAutoIncrementAsync(TablePlan table, long? sourceNext, CancellationToken cancellationToken);
}

/// <summary>接続を開く（テストでは偽物に差し替える）。</summary>
public interface IDatabaseOpener
{
    Task<ISourceDatabase> OpenSourceAsync(Dbms dbms, string connectionString, int commandTimeoutSeconds, CancellationToken cancellationToken);

    Task<ITargetDatabase> OpenTargetAsync(Dbms dbms, string connectionString, int commandTimeoutSeconds, CancellationToken cancellationToken);
}
