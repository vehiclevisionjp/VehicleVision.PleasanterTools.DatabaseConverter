using System.Globalization;

namespace VehicleVision.PleasanterTools.DatabaseConverter.Core.Engine;

/// <summary>進み具合と結果を出す先。メッセージは英語。</summary>
public interface IConversionLog
{
    void Info(string message);

    void Warn(string message);

    void Error(string message);
}

/// <summary>コンソールへ出す <see cref="IConversionLog"/>。</summary>
public sealed class ConsoleConversionLog : IConversionLog
{
    private readonly TextWriter _out;
    private readonly TextWriter _err;

    public ConsoleConversionLog(TextWriter output, TextWriter error)
    {
        _out = output;
        _err = error;
    }

    public void Info(string message) => _out.WriteLine(SecretRedactor.Redact(message));

    public void Warn(string message) => _out.WriteLine("WARNING: " + SecretRedactor.Redact(message));

    public void Error(string message) => _err.WriteLine("ERROR: " + SecretRedactor.Redact(message));
}

/// <summary>表の結果。</summary>
public enum TableStatus
{
    /// <summary>まだ処理していない（事前確認で止まった、など）。</summary>
    NotRun,

    /// <summary>コピーして、件数と内容の整合を確かめた。</summary>
    Copied,

    /// <summary>完了の記録があるので、とばした（件数だけ確かめた）。</summary>
    Skipped,

    /// <summary>失敗（行の失敗・整合の不一致・表の定義の不一致など）。</summary>
    Failed,

    /// <summary>ドライラン（何も書いていない）。</summary>
    DryRun,
}

/// <summary>失敗した行・列。</summary>
/// <param name="Location">行の位置（<c>row #N PK[Id=5]</c> など）。</param>
/// <param name="Column">列名。特定できなければ <see langword="null"/>。</param>
/// <param name="Message">理由（英語）。</param>
public sealed record RowFailureDetail(string Location, string? Column, string Message);

/// <summary>1 つの表の結果。</summary>
public sealed class TableResult
{
    /// <summary>1 つの表について保持する失敗の詳細の上限（件数は全部数える）。</summary>
    public const int MaxStoredFailures = 100;

    private readonly List<RowFailureDetail> _failures = [];
    private readonly List<string> _errors = [];
    private readonly List<string> _warnings = [];

    public TableResult(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public TableStatus Status { get; set; } = TableStatus.NotRun;

    /// <summary>変換元の行数。</summary>
    public long SourceRows { get; set; }

    /// <summary>変換先へ入れた行数。</summary>
    public long CopiedRows { get; set; }

    /// <summary>入れられなかった行数（変換できない値・変換先が拒否した行）。</summary>
    public long FailedRows { get; set; }

    /// <summary>変換先の精度へ丸めた値の数（日時の小数秒・単精度など）。</summary>
    public long RoundedValues { get; set; }

    /// <summary>件数と内容の整合を確かめて一致したか。</summary>
    public bool Verified { get; set; }

    /// <summary>メモ（とばした理由など。英語）。</summary>
    public string? Note { get; set; }

    /// <summary>表の単位の失敗（定義の不一致・整合の不一致など。英語）。</summary>
    public IReadOnlyList<string> Errors => _errors;

    /// <summary>警告（英語）。</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>失敗した行の詳細（先頭から <see cref="MaxStoredFailures"/> 件）。</summary>
    public IReadOnlyList<RowFailureDetail> Failures => _failures;

    /// <summary>失敗の数（表の単位の失敗 + 失敗した行）。</summary>
    public long FailureCount => _errors.Count + FailedRows;

    public bool HasFailures => FailureCount > 0 || Status == TableStatus.Failed;

    public void AddError(string message)
    {
        _errors.Add(message);
        Status = TableStatus.Failed;
    }

    public void AddWarning(string message) => _warnings.Add(message);

    public void AddRowFailure(RowFailureDetail detail)
    {
        FailedRows++;
        Status = TableStatus.Failed;
        if (_failures.Count < MaxStoredFailures)
        {
            _failures.Add(detail);
        }
    }
}

/// <summary>変換全体の結果。</summary>
public sealed class ConversionReport
{
    private readonly List<string> _errors = [];
    private readonly List<string> _warnings = [];

    public ConversionReport(bool dryRun)
    {
        DryRun = dryRun;
    }

    public bool DryRun { get; }

    /// <summary>中断された（Ctrl+C など）か。</summary>
    public bool Cancelled { get; set; }

    public List<TableResult> Tables { get; } = [];

    /// <summary>全体の失敗（英語）。</summary>
    public IReadOnlyList<string> Errors => _errors;

    /// <summary>全体の警告（英語）。</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    public void AddError(string message) => _errors.Add(message);

    public void AddWarning(string message) => _warnings.Add(message);

    /// <summary>失敗の総数。</summary>
    public long FailureCount => _errors.Count + Tables.Sum(t => t.FailureCount) + (Cancelled ? 1 : 0);

    public bool HasFailures => FailureCount > 0 || Tables.Any(t => t.Status == TableStatus.Failed);

    /// <summary>終了コード: 全件成功 0、失敗があれば 1。</summary>
    public int ExitCode => HasFailures ? 1 : 0;

    private const int MaxSummaryWarningsPerTable = 3;

    /// <summary>表ごとの件数・とばした数・失敗の一覧を出す。</summary>
    public void WriteSummary(IConversionLog log)
    {
        log.Info(string.Empty);
        log.Info(DryRun ? "=== Dry-run summary (nothing was written) ===" : "=== Summary ===");

        var nameWidth = Math.Max(5, Tables.Select(t => t.Name.Length).DefaultIfEmpty(5).Max());
        log.Info(string.Format(
            CultureInfo.InvariantCulture,
            "{0}  {1,-9}  {2,12}  {3,12}  {4,8}  {5,8}  {6}",
            "Table".PadRight(nameWidth),
            "Status",
            "Source rows",
            "Copied rows",
            "Failed",
            "Rounded",
            "Verified"));
        foreach (var t in Tables)
        {
            log.Info(string.Format(
                CultureInfo.InvariantCulture,
                "{0}  {1,-9}  {2,12}  {3,12}  {4,8}  {5,8}  {6}",
                t.Name.PadRight(nameWidth),
                StatusText(t.Status),
                t.SourceRows,
                t.CopiedRows,
                t.FailureCount,
                t.RoundedValues,
                t.Verified ? "yes" : "no"));
        }

        var copied = Tables.Count(t => t.Status == TableStatus.Copied);
        var skipped = Tables.Count(t => t.Status == TableStatus.Skipped);
        var failed = Tables.Count(t => t.Status == TableStatus.Failed);
        var notRun = Tables.Count(t => t.Status == TableStatus.NotRun);
        var dry = Tables.Count(t => t.Status == TableStatus.DryRun);
        log.Info(string.Format(
            CultureInfo.InvariantCulture,
            "Tables: {0} copied, {1} skipped (already completed), {2} failed, {3} not run, {4} dry-run. Rows copied: {5}.",
            copied,
            skipped,
            failed,
            notRun,
            dry,
            Tables.Sum(t => t.CopiedRows)));

        foreach (var warning in _warnings)
        {
            log.Warn(warning);
        }

        foreach (var t in Tables)
        {
            // 警告が多い表は、先頭の数件だけを出す（ドライランでは表ごとに全部出している）
            foreach (var w in t.Warnings.Take(MaxSummaryWarningsPerTable))
            {
                log.Warn($"[{t.Name}] {w}");
            }

            if (t.Warnings.Count > MaxSummaryWarningsPerTable)
            {
                log.Warn($"[{t.Name}] ... and {t.Warnings.Count - MaxSummaryWarningsPerTable} more warning(s)");
            }
        }

        if (HasFailures)
        {
            log.Error($"{FailureCount} failure(s):");
            foreach (var error in _errors)
            {
                log.Error($"  {error}");
            }

            foreach (var t in Tables.Where(t => t.FailureCount > 0 || t.Status == TableStatus.Failed))
            {
                foreach (var error in t.Errors)
                {
                    log.Error($"  [{t.Name}] {error}");
                }

                foreach (var f in t.Failures)
                {
                    var column = f.Column is null ? string.Empty : $" column '{f.Column}':";
                    log.Error($"  [{t.Name}] {f.Location}{column} {f.Message}");
                }

                if (t.FailedRows > t.Failures.Count)
                {
                    log.Error($"  [{t.Name}] ... and {t.FailedRows - t.Failures.Count} more failed row(s) not listed");
                }
            }

            if (Cancelled)
            {
                log.Error("  The run was interrupted before it finished.");
            }

            log.Error("Result: FAILED");
        }
        else
        {
            log.Info(DryRun ? "Result: OK (dry-run; no problems found)" : "Result: OK");
        }
    }

    private static string StatusText(TableStatus status) => status switch
    {
        TableStatus.NotRun => "NotRun",
        TableStatus.Copied => "Copied",
        TableStatus.Skipped => "Skipped",
        TableStatus.Failed => "FAILED",
        TableStatus.DryRun => "DryRun",
        _ => status.ToString(),
    };
}

/// <summary>
/// ログ・結果へ出す文字列から、接続文字列やパスワードを伏せる。
/// 接続文字列を解決した時点で <see cref="Register"/> する。
/// </summary>
public static class SecretRedactor
{
    private static readonly object Gate = new();
    private static readonly List<string> Secrets = [];

    /// <summary>伏せる文字列を登録する。空や短すぎるもの（置換で無関係な語を壊す）は無視する。</summary>
    public static void Register(string? secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length < 3)
        {
            return;
        }

        lock (Gate)
        {
            if (!Secrets.Contains(secret, StringComparer.Ordinal))
            {
                Secrets.Add(secret);
                Secrets.Sort((a, b) => b.Length.CompareTo(a.Length));
            }
        }
    }

    /// <summary>接続文字列から、パスワードと接続文字列全体を登録する。</summary>
    public static void RegisterConnectionString(string connectionString)
    {
        Register(connectionString);
        try
        {
            var builder = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString };
            foreach (var key in new[] { "password", "pwd" })
            {
                if (builder.TryGetValue(key, out var value))
                {
                    Register(value?.ToString());
                }
            }
        }
        catch (ArgumentException)
        {
            // 書式が不正な接続文字列は全体の登録だけで足りる
        }
    }

    /// <summary>登録済みの文字列を <c>***</c> に置き換える。</summary>
    public static string Redact(string text)
    {
        lock (Gate)
        {
            foreach (var secret in Secrets)
            {
                text = text.Replace(secret, "***", StringComparison.Ordinal);
            }
        }

        return text;
    }

    /// <summary>登録を消す（テスト用）。</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Secrets.Clear();
        }
    }
}
