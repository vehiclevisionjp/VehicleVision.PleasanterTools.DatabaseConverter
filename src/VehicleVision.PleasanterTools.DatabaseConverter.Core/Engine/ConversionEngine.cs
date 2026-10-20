using System.Diagnostics;
using System.Globalization;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Databases;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Planning;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Schema;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Values;

namespace VehicleVision.PleasanterTools.DatabaseConverter.Core.Engine;

/// <summary>変換の実行の指定。</summary>
public sealed class RunOptions
{
    /// <summary>既定のバッチの行数。</summary>
    public const int DefaultBatchSize = 1000;

    /// <summary>既定のバッチの概算バイト数の上限（Binaries.Bin のような大きな列で、メモリと 1 回の書き込みが膨らまないように）。</summary>
    public const long DefaultMaxBatchBytes = 4 * 1024 * 1024;

    /// <summary>何も書かず、何をどれだけ移すかだけ出す。</summary>
    public bool DryRun { get; init; }

    /// <summary>完了を記録した表をとばし、途中までの表を最初から入れ直す。</summary>
    public bool Resume { get; init; }

    /// <summary>バッチの行数。</summary>
    public int BatchSize { get; init; } = DefaultBatchSize;

    /// <summary>バッチの概算バイト数の上限。超えたら行数に達していなくても書く。</summary>
    public long MaxBatchBytes { get; init; } = DefaultMaxBatchBytes;

    /// <summary>外部キーなどの制約を、変換が終わるまで一時的に無効にする。</summary>
    public bool DisableConstraints { get; init; }

    /// <summary>全表が成功したら、管理表を消す。</summary>
    public bool CleanupProgress { get; init; }

    /// <summary>進み具合の行を出す間隔（秒）。</summary>
    public double ProgressIntervalSeconds { get; init; } = 5;
}

/// <summary>
/// 変換の本体。<see cref="ISourceDatabase"/> と <see cref="ITargetDatabase"/> にだけ依存する。
/// </summary>
public sealed class ConversionEngine
{
    private readonly ISourceDatabase _source;
    private readonly ITargetDatabase _target;
    private readonly IConversionLog _log;

    public ConversionEngine(ISourceDatabase source, ITargetDatabase target, IConversionLog log)
    {
        _source = source;
        _target = target;
        _log = log;
    }

    private enum TableAction
    {
        Copy,
        ClearAndCopy,
        Skip,
        Blocked,
    }

    /// <summary>計画を実行する。</summary>
    public async Task<ConversionReport> RunAsync(ConversionPlan plan, RunOptions options, CancellationToken cancellationToken)
    {
        var report = new ConversionReport(options.DryRun);
        foreach (var error in plan.Errors)
        {
            report.AddError(error);
        }

        foreach (var excluded in plan.ExcludedTables)
        {
            _log.Info($"Excluded: {excluded}");
        }

        if (plan.TargetOnlyTables.Count > 0)
        {
            _log.Info($"Tables only in the target (nothing to copy): {string.Join(", ", plan.TargetOnlyTables)}");
        }

        var results = plan.Tables.ToDictionary(t => t, t => new TableResult(t.Name));
        report.Tables.AddRange(plan.Tables.Select(t => results[t]));

        try
        {
            var actions = await PreflightAsync(plan, options, results, cancellationToken).ConfigureAwait(false);
            var blocked = report.Errors.Count > 0 || results.Values.Any(r => r.Status == TableStatus.Failed);
            if (options.DryRun)
            {
                foreach (var table in plan.Tables)
                {
                    DescribeDryRun(table, results[table], actions[table]);
                }

                return report;
            }

            if (blocked)
            {
                report.AddError("Preflight checks failed; nothing was written. Fix the problems above (or exclude the tables) and run again.");
                return report;
            }

            await _target.EnsureProgressTableAsync(cancellationToken).ConfigureAwait(false);
            await CopyAllAsync(plan, options, results, actions, report, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            report.Cancelled = true;
            _log.Error("Interrupted. Tables not marked as completed will be reloaded from the start with --resume.");
        }

        return report;
    }

    // 事前確認: 何も書かない。表の定義の不一致、変換先が空でない、を見つける
    private async Task<Dictionary<TablePlan, TableAction>> PreflightAsync(
        ConversionPlan plan,
        RunOptions options,
        Dictionary<TablePlan, TableResult> results,
        CancellationToken ct)
    {
        var completed = await _target.ReadCompletedTablesAsync(ct).ConfigureAwait(false);
        var actions = new Dictionary<TablePlan, TableAction>();
        foreach (var table in plan.Tables)
        {
            var result = results[table];
            actions[table] = TableAction.Blocked;
            foreach (var warning in table.Warnings)
            {
                result.AddWarning(warning);
            }

            foreach (var error in table.Errors)
            {
                result.AddError(error);
            }

            result.SourceRows = await _source.CountRowsAsync(table.Name, ct).ConfigureAwait(false);
            if (table.IsSourceOnly)
            {
                if (result.SourceRows > 0)
                {
                    result.AddError($"the table exists only in the source and has {result.SourceRows} row(s) that would not be copied; create it in the target with the Pleasanter CodeDefiner, or exclude it with --exclude-tables");
                }
                else
                {
                    result.AddWarning("the table exists only in the source; it is empty, so nothing is lost");
                    result.Status = TableStatus.Skipped;
                    result.Verified = true;
                    result.Note = "exists only in the source (empty)";
                }

                continue;
            }

            if (!table.IsRunnable)
            {
                continue;
            }

            var targetName = table.Target!.Name;
            var targetRows = await _target.CountRowsAsync(targetName, ct).ConfigureAwait(false);
            if (completed.Contains(targetName) && options.Resume)
            {
                actions[table] = TableAction.Skip;
            }
            else if (targetRows > 0)
            {
                if (options.Resume)
                {
                    actions[table] = TableAction.ClearAndCopy;
                    result.AddWarning($"the target table has {targetRows} row(s) from an unfinished run; they will be deleted and the table reloaded from the start");
                }
                else
                {
                    result.AddError($"the target table is not empty ({targetRows} row(s)). Use --resume to skip completed tables and reload unfinished ones, or empty the table");
                }
            }
            else
            {
                actions[table] = TableAction.Copy;
            }
        }

        return actions;
    }

    private void DescribeDryRun(TablePlan table, TableResult result, TableAction action)
    {
        if (result.Status == TableStatus.Failed)
        {
            _log.Error($"[dry-run] {table.Name}: cannot be copied");
            foreach (var error in result.Errors)
            {
                _log.Error($"[dry-run] {table.Name}: {error}");
            }

            return;
        }

        if (table.IsSourceOnly)
        {
            _log.Info($"[dry-run] {table.Name}: exists only in the source (empty); skipped");
            return;
        }

        var verb = action switch
        {
            TableAction.Skip => "would skip (already completed)",
            TableAction.ClearAndCopy => "would delete the unfinished target rows and copy",
            _ => "would copy",
        };
        var columns = string.Join(", ", table.Mappings.Select(m => m.Target.Name));
        var identity = table.AutoIncrementIsMapped ? $"; auto-increment '{table.AutoIncrement!.Name}' will be carried over" : string.Empty;
        _log.Info(string.Format(
            CultureInfo.InvariantCulture,
            "[dry-run] {0}: {1} {2} row(s), {3} column(s){4}",
            table.Name,
            verb,
            result.SourceRows,
            table.Mappings.Count,
            identity));
        _log.Info($"[dry-run]   columns: {columns}");
        foreach (var warning in result.Warnings)
        {
            _log.Warn($"[dry-run] {table.Name}: {warning}");
        }

        if (action != TableAction.Skip)
        {
            result.Status = TableStatus.DryRun;
        }
        else
        {
            result.Status = TableStatus.DryRun;
            result.Note = "already completed";
        }
    }

    private async Task CopyAllAsync(
        ConversionPlan plan,
        RunOptions options,
        Dictionary<TablePlan, TableResult> results,
        Dictionary<TablePlan, TableAction> actions,
        ConversionReport report,
        CancellationToken ct)
    {
        IConstraintScope? scope = null;
        var runnable = plan.Tables.Where(t => t.IsRunnable && actions[t] != TableAction.Skip).ToList();
        if (options.DisableConstraints && runnable.Count > 0)
        {
            try
            {
                scope = await _target.DisableConstraintsAsync(runnable.Select(t => t.Target!.Name).ToList(), ct).ConfigureAwait(false);
                if (scope.Warning is not null)
                {
                    report.AddWarning(scope.Warning);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                report.AddWarning($"could not disable constraints ({Describe(ex)}); relying on the parent-to-child copy order");
            }
        }

        try
        {
            var index = 0;
            foreach (var table in plan.Tables)
            {
                ct.ThrowIfCancellationRequested();
                index++;
                var result = results[table];
                if (!table.IsRunnable)
                {
                    continue;
                }

                _log.Info($"[{index}/{plan.Tables.Count}] {table.Name}: {result.SourceRows} row(s)");
                try
                {
                    if (actions[table] == TableAction.Skip)
                    {
                        await SkipTableAsync(table, result, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        if (actions[table] == TableAction.ClearAndCopy)
                        {
                            await _target.ClearTableAsync(table.Target!.Name, ct).ConfigureAwait(false);
                        }

                        await CopyTableAsync(table, result, options, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.AddError($"unexpected error: {Describe(ex)}");
                    _log.Error($"[{table.Name}] unexpected error: {Describe(ex)}");
                }
            }
        }
        finally
        {
            if (scope is not null)
            {
                try
                {
                    foreach (var problem in await scope.RestoreAsync(CancellationToken.None).ConfigureAwait(false))
                    {
                        report.AddError($"constraint check after the copy: {problem}");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    report.AddError($"could not restore constraints: {Describe(ex)}");
                }
            }
        }

        if (options.CleanupProgress && !report.HasFailures && !ct.IsCancellationRequested)
        {
            await _target.DropProgressTableAsync(ct).ConfigureAwait(false);
            _log.Info("Dropped the progress table.");
        }
    }

    // 完了の記録がある表: 件数だけ確かめる
    private async Task SkipTableAsync(TablePlan table, TableResult result, CancellationToken ct)
    {
        var targetRows = await _target.CountRowsAsync(table.Target!.Name, ct).ConfigureAwait(false);
        result.CopiedRows = targetRows;
        if (targetRows != result.SourceRows)
        {
            result.AddError(
                $"marked as completed, but the row count differs (source {result.SourceRows}, target {targetRows}); empty the target table and run with --resume to reload it");
            return;
        }

        result.Status = TableStatus.Skipped;
        result.Verified = true;
        result.Note = "already completed";
        _log.Info($"[{table.Name}] skipped: already completed ({targetRows} row(s) match)");
    }

    private async Task CopyTableAsync(TablePlan table, TableResult result, RunOptions options, CancellationToken ct)
    {
        var converter = new ValueConverter(_target.Dbms);
        var targetNames = table.Mappings.Select(m => m.Target.Name).ToList();
        var targetTypes = table.Mappings.Select(m => m.Target.Type).ToList();
        var expected = new TableDigest(targetNames, targetTypes, table.TargetKeyIndexes);
        var sourceNames = table.Mappings.Select(m => m.Source.Name).ToList();

        var batch = new List<object?[]>(Math.Min(options.BatchSize, 1024));
        var batchRowNumbers = new List<long>(batch.Capacity);
        long batchBytes = 0;
        long rowNumber = 0;
        var clock = Stopwatch.StartNew();
        var lastProgress = TimeSpan.Zero;

        async Task FlushAsync()
        {
            if (batch.Count == 0)
            {
                return;
            }

            var written = await _target.WriteBatchAsync(table, batch, ct).ConfigureAwait(false);
            var failedIndexes = new HashSet<int>(written.Failures.Select(f => f.RowIndex));
            foreach (var failure in written.Failures)
            {
                var location = DescribeRow(table.TargetKeyIndexes, targetNames, batch[failure.RowIndex], batchRowNumbers[failure.RowIndex]);
                result.AddRowFailure(new RowFailureDetail(location, null, FirstLine(failure.Message)));
            }

            for (var i = 0; i < batch.Count; i++)
            {
                if (!failedIndexes.Contains(i))
                {
                    expected.Add(batch[i]);
                }
            }

            result.CopiedRows += batch.Count - failedIndexes.Count;
            batch.Clear();
            batchRowNumbers.Clear();
            batchBytes = 0;

            if (clock.Elapsed - lastProgress >= TimeSpan.FromSeconds(options.ProgressIntervalSeconds))
            {
                lastProgress = clock.Elapsed;
                _log.Info($"[{table.Name}] {result.CopiedRows}/{result.SourceRows} row(s) copied");
            }
        }

        await _target.BeginTableAsync(table, ct).ConfigureAwait(false);
        try
        {
            await foreach (var row in _source.ReadRowsAsync(table.Name, sourceNames, table.Source.PrimaryKey, ct).ConfigureAwait(false))
            {
                rowNumber++;
                var converted = new object?[table.Mappings.Count];
                var ok = true;
                for (var i = 0; i < converted.Length && ok; i++)
                {
                    var mapping = table.Mappings[i];
                    var value = row[i];
                    if (value is null or DBNull)
                    {
                        if (!mapping.Target.IsNullable)
                        {
                            RecordConversionFailure(table, result, row, rowNumber, mapping.Source.Name, "NULL cannot be stored in a NOT NULL target column");
                            ok = false;
                        }

                        continue;
                    }

                    try
                    {
                        converted[i] = converter.Convert(value, mapping.Source.Type, mapping.Target.Type, out var rounded);
                        if (rounded)
                        {
                            result.RoundedValues++;
                        }
                    }
                    catch (ValueConversionException ex)
                    {
                        RecordConversionFailure(table, result, row, rowNumber, mapping.Source.Name, ex.Message);
                        ok = false;
                    }
                }

                if (!ok)
                {
                    continue;
                }

                batch.Add(converted);
                batchRowNumbers.Add(rowNumber);
                batchBytes += EstimateBytes(converted);
                if (batch.Count >= options.BatchSize || batchBytes >= options.MaxBatchBytes)
                {
                    await FlushAsync().ConfigureAwait(false);
                }
            }

            await FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            await _target.EndTableAsync(table, CancellationToken.None).ConfigureAwait(false);
        }

        await AdjustAutoIncrementAsync(table, result, ct).ConfigureAwait(false);
        await VerifyAsync(table, result, expected, targetNames, targetTypes, ct).ConfigureAwait(false);

        if (result.RoundedValues > 0)
        {
            result.AddWarning($"{result.RoundedValues} value(s) were rounded to the target precision (date/time fractional seconds or single-precision floats)");
        }

        if (result.FailureCount == 0)
        {
            result.Status = TableStatus.Copied;
            await _target.MarkCompletedAsync(table.Target!.Name, result.CopiedRows, ct).ConfigureAwait(false);
            _log.Info($"[{table.Name}] done: {result.CopiedRows} row(s) copied and verified");
        }
        else
        {
            result.Status = TableStatus.Failed;
            _log.Error($"[{table.Name}] finished with {result.FailureCount} failure(s); the table is not marked as completed");
        }
    }

    private async Task AdjustAutoIncrementAsync(TablePlan table, TableResult result, CancellationToken ct)
    {
        if (table.AutoIncrement is null || !table.AutoIncrementIsMapped)
        {
            return;
        }

        var sourceColumn = table.Mappings
            .First(m => string.Equals(m.Target.Name, table.AutoIncrement.Name, StringComparison.Ordinal))
            .Source.Name;
        long? sourceNext = null;
        try
        {
            sourceNext = await _source.ReadNextAutoIncrementAsync(table.Name, sourceColumn, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.AddWarning($"could not read the source auto-increment counter of '{sourceColumn}' ({Describe(ex)}); the counter is set from the maximum value in the target");
        }

        if (sourceNext is null)
        {
            result.AddWarning($"the source auto-increment counter of '{sourceColumn}' is unavailable; the counter is set from the maximum value in the target");
        }

        try
        {
            var next = await _target.AdjustAutoIncrementAsync(table, sourceNext, ct).ConfigureAwait(false);
            if (next is not null)
            {
                _log.Info($"[{table.Name}] auto-increment '{table.AutoIncrement.Name}' continues from {next}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.AddError($"could not set the auto-increment counter of '{table.AutoIncrement.Name}': {Describe(ex)}");
        }
    }

    // 変換先の内容を読み戻し、書いた内容の要約と突き合わせる
    private async Task VerifyAsync(
        TablePlan table,
        TableResult result,
        TableDigest expected,
        IReadOnlyList<string> targetNames,
        IReadOnlyList<ColumnType> targetTypes,
        CancellationToken ct)
    {
        try
        {
            var actual = new TableDigest(targetNames, targetTypes, table.TargetKeyIndexes);
            await foreach (var row in _target.ReadRowsAsync(table.Target!.Name, targetNames, [], ct).ConfigureAwait(false))
            {
                actual.Add(row);
            }

            var differences = expected.Compare(actual);
            if (differences.Count == 0)
            {
                result.Verified = result.FailedRows == 0;
                return;
            }

            foreach (var difference in differences)
            {
                result.AddError($"verification failed: {difference}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.AddError($"verification could not be completed: {Describe(ex)}");
        }
    }

    private void RecordConversionFailure(TablePlan table, TableResult result, object?[] sourceRow, long rowNumber, string column, string message)
    {
        var names = table.Mappings.Select(m => m.Source.Name).ToList();
        var location = DescribeRow(table.SourceKeyIndexes, names, sourceRow, rowNumber);
        result.AddRowFailure(new RowFailureDetail(location, column, message));
    }

    /// <summary>行の位置を「row #N PK[Id=5]」の形にする。</summary>
    internal static string DescribeRow(IReadOnlyList<int> keyIndexes, IReadOnlyList<string> names, IReadOnlyList<object?> row, long rowNumber)
    {
        if (keyIndexes.Count == 0)
        {
            return $"row #{rowNumber} (no primary key)";
        }

        var parts = keyIndexes.Select(i => $"{names[i]}={FormatValue(row[i])}");
        return $"row #{rowNumber} PK[{string.Join(", ", parts)}]";
    }

    private static string FormatValue(object? value) => value switch
    {
        null or DBNull => "NULL",
        string s => s.Length > 40 ? $"'{s[..40]}...'" : $"'{s}'",
        byte[] b => $"<{b.Length} bytes>",
        DateTime d => ValueConverter.FormatDateTime(d),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>行のおおよその大きさ（バッチをバイト数でも区切るための目安）。</summary>
    internal static long EstimateBytes(IReadOnlyList<object?> row)
    {
        long total = 0;
        foreach (var value in row)
        {
            total += value switch
            {
                null => 1,
                string s => s.Length * 2L,
                byte[] b => b.Length,
                _ => 16,
            };
        }

        return total;
    }

    private static string FirstLine(string message)
    {
        var text = SecretRedactor.Redact(message);
        var newline = text.IndexOfAny(['\r', '\n']);
        if (newline >= 0)
        {
            text = text[..newline];
        }

        return text.Length > 300 ? text[..300] + "..." : text;
    }

    internal static string Describe(Exception ex) => $"{ex.GetType().Name}: {FirstLine(ex.Message)}";
}
