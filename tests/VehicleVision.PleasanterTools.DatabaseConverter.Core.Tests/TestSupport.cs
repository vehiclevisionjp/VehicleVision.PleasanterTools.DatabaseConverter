using System.Runtime.CompilerServices;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Databases;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Engine;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Planning;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Schema;

namespace VehicleVision.PleasanterTools.DatabaseConverter.Core.Tests;

/// <summary>テスト用のスキーマの組み立て。</summary>
internal static class T
{
    public static readonly ColumnType BigInt = new(ValueKind.Integer, "bigint");
    public static readonly ColumnType Int = new(ValueKind.Integer, "int", IntegerMin: int.MinValue, IntegerMax: int.MaxValue);
    public static readonly ColumnType Bit = new(ValueKind.Boolean, "bit");
    public static readonly ColumnType Text = new(ValueKind.String, "text");

    public static ColumnType VarChar(long length, LengthUnit unit = LengthUnit.CodePoints)
        => new(ValueKind.String, "varchar", length, unit);

    public static readonly ColumnType Bytes = new(ValueKind.Binary, "bytea");

    public static ColumnType Decimal(int p, int s) => new(ValueKind.Decimal, "numeric", Precision: p, Scale: s);

    public static ColumnType DateTime(int digits) => new(ValueKind.DateTime, "timestamp", FractionDigits: digits);

    public static ColumnType LegacyDateTime { get; } = new(ValueKind.DateTime, "datetime", FractionDigits: 3, IsLegacyDateTime: true);

    public static ColumnInfo Col(
        string name,
        ColumnType type,
        bool nullable = true,
        bool hasDefault = false,
        bool auto = false,
        bool readOnly = false,
        int ordinal = 0)
        => new(name, ordinal, type, nullable, hasDefault, auto, false, readOnly);

    public static TableInfo Table(string name, ColumnInfo[] columns, string[]? pk = null, string[]? parents = null)
        => new(
            name,
            columns.Select((c, i) => c with { Ordinal = i + 1 }).ToList(),
            pk ?? [],
            parents ?? []);

    public static SchemaInfo Schema(Dbms dbms, params TableInfo[] tables) => new(dbms, tables);
}

/// <summary>ログを溜める。</summary>
internal sealed class ListLog : IConversionLog
{
    public List<string> Lines { get; } = [];

    public void Info(string message) => Lines.Add(message);

    public void Warn(string message) => Lines.Add("WARNING: " + message);

    public void Error(string message) => Lines.Add("ERROR: " + message);

    public string Text => string.Join(Environment.NewLine, Lines);
}

/// <summary>メモリ上の偽の DB。変換元にも変換先にも使う。</summary>
internal sealed class InMemoryDatabase : ITargetDatabase
{
    private readonly Dictionary<string, TableInfo> _tables = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<object?[]>> _rows = new(StringComparer.Ordinal);

    public InMemoryDatabase(Dbms dbms)
    {
        Dbms = dbms;
    }

    public Dbms Dbms { get; }

    /// <summary>書き込み系のメソッドが呼ばれた回数（変換元・ドライランで 0 であることを確かめる）。</summary>
    public int WriteCalls { get; private set; }

    public HashSet<string> Completed { get; } = new(StringComparer.Ordinal);

    public bool ProgressTableExists { get; private set; }

    public List<(string Table, int Rows)> Batches { get; } = [];

    public Dictionary<string, long?> SourceNextAutoIncrement { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, long?> AdjustedAutoIncrement { get; } = new(StringComparer.Ordinal);

    public List<string> Cleared { get; } = [];

    /// <summary>行を拒否する（例: 一意制約違反）。メッセージを返すと、その行は入らない。</summary>
    public Func<string, object?[], string?>? RejectRow { get; set; }

    /// <summary>書き込んだ値を変える（読み戻しで不一致を起こす）。</summary>
    public Func<string, object?[], object?[]>? CorruptOnWrite { get; set; }

    public bool ConstraintsDisabled { get; private set; }

    public bool ConstraintsRestored { get; private set; }

    public InMemoryDatabase Add(TableInfo table, params object?[][] rows)
    {
        _tables[table.Name] = table;
        _rows[table.Name] = rows.ToList();
        return this;
    }

    public IReadOnlyList<object?[]> Rows(string table) => _rows[table];

    public Task<SchemaInfo> ReadSchemaAsync(CancellationToken cancellationToken)
        => Task.FromResult(new SchemaInfo(Dbms, _tables.Values.ToList()));

    public Task<long> CountRowsAsync(string table, CancellationToken cancellationToken)
        => Task.FromResult((long)_rows[table].Count);

    public async IAsyncEnumerable<object?[]> ReadRowsAsync(
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> orderBy,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var info = _tables[table];
        var indexes = columns.Select(c => info.Columns.ToList().FindIndex(x => x.Name == c)).ToArray();
        foreach (var row in _rows[table].ToList())
        {
            await Task.Yield();
            yield return indexes.Select(i => row[i] ?? DBNull.Value).ToArray();
        }
    }

    public Task<long?> ReadNextAutoIncrementAsync(string table, string column, CancellationToken cancellationToken)
        => Task.FromResult(SourceNextAutoIncrement.GetValueOrDefault(table));

    public Task EnsureProgressTableAsync(CancellationToken cancellationToken)
    {
        WriteCalls++;
        ProgressTableExists = true;
        return Task.CompletedTask;
    }

    public Task<IReadOnlySet<string>> ReadCompletedTablesAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(Completed, StringComparer.Ordinal));

    public Task MarkCompletedAsync(string table, long rows, CancellationToken cancellationToken)
    {
        WriteCalls++;
        Completed.Add(table);
        return Task.CompletedTask;
    }

    public Task DropProgressTableAsync(CancellationToken cancellationToken)
    {
        WriteCalls++;
        ProgressTableExists = false;
        Completed.Clear();
        return Task.CompletedTask;
    }

    public Task ClearTableAsync(string table, CancellationToken cancellationToken)
    {
        WriteCalls++;
        _rows[table].Clear();
        Cleared.Add(table);
        return Task.CompletedTask;
    }

    public Task<IConstraintScope> DisableConstraintsAsync(IReadOnlyList<string> tables, CancellationToken cancellationToken)
    {
        WriteCalls++;
        ConstraintsDisabled = true;
        return Task.FromResult<IConstraintScope>(new Scope(this));
    }

    public Task BeginTableAsync(TablePlan table, CancellationToken cancellationToken)
    {
        WriteCalls++;
        return Task.CompletedTask;
    }

    public Task EndTableAsync(TablePlan table, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<WriteBatchResult> WriteBatchAsync(TablePlan table, IReadOnlyList<object?[]> rows, CancellationToken cancellationToken)
    {
        WriteCalls++;
        var name = table.Target!.Name;
        Batches.Add((name, rows.Count));
        var info = _tables[name];
        var failures = new List<RowWriteFailure>();
        for (var i = 0; i < rows.Count; i++)
        {
            var message = RejectRow?.Invoke(name, rows[i]);
            if (message is not null)
            {
                failures.Add(new RowWriteFailure(i, message));
                continue;
            }

            var values = CorruptOnWrite?.Invoke(name, rows[i]) ?? rows[i];
            var stored = new object?[info.Columns.Count];
            for (var m = 0; m < table.Mappings.Count; m++)
            {
                var index = info.Columns.ToList().FindIndex(c => c.Name == table.Mappings[m].Target.Name);
                stored[index] = values[m];
            }

            _rows[name].Add(stored);
        }

        return Task.FromResult(new WriteBatchResult(rows.Count - failures.Count, failures));
    }

    public Task<long?> AdjustAutoIncrementAsync(TablePlan table, long? sourceNext, CancellationToken cancellationToken)
    {
        WriteCalls++;
        var column = table.AutoIncrement!.Name;
        var index = _tables[table.Target!.Name].Columns.ToList().FindIndex(c => c.Name == column);
        var max = _rows[table.Target.Name].Select(r => r[index]).OfType<long>().DefaultIfEmpty(0).Max();
        var next = Math.Max(sourceNext ?? 0, max + 1);
        AdjustedAutoIncrement[table.Target.Name] = next;
        return Task.FromResult<long?>(next);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class Scope : IConstraintScope
    {
        private readonly InMemoryDatabase _owner;

        public Scope(InMemoryDatabase owner)
        {
            _owner = owner;
        }

        public string? Warning => null;

        public Task<IReadOnlyList<string>> RestoreAsync(CancellationToken cancellationToken)
        {
            _owner.ConstraintsRestored = true;
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }
}
