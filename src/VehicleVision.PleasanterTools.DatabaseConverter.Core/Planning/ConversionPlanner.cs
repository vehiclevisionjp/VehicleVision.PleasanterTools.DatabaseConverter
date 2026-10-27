using VehicleVision.PleasanterTools.DatabaseConverter.Core.Schema;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Values;

namespace VehicleVision.PleasanterTools.DatabaseConverter.Core.Planning;

/// <summary>変換元の列と変換先の列の対応。</summary>
/// <param name="Source">変換元の列。</param>
/// <param name="Target">変換先の列。</param>
/// <param name="Warning">型の違いによる注意（英語）。無ければ <see langword="null"/>。</param>
public sealed record ColumnMapping(ColumnInfo Source, ColumnInfo Target, string? Warning);

/// <summary>1 つの表の変換計画。</summary>
public sealed class TablePlan
{
    public TablePlan(
        TableInfo source,
        TableInfo? target,
        IReadOnlyList<ColumnMapping> mappings,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> errors)
    {
        Source = source;
        Target = target;
        Mappings = mappings;
        Warnings = warnings;
        Errors = errors;
        SourceKeyIndexes = ResolveKeyIndexes(source.PrimaryKey, mappings.Select(m => m.Source.Name).ToList());
        TargetKeyIndexes = target is null
            ? []
            : ResolveKeyIndexes(target.PrimaryKey, mappings.Select(m => m.Target.Name).ToList());
        AutoIncrement = target?.Columns.FirstOrDefault(c => c.IsAutoIncrement && !c.IsReadOnly);
        AutoIncrementIsMapped = AutoIncrement is not null
            && mappings.Any(m => string.Equals(m.Target.Name, AutoIncrement.Name, StringComparison.Ordinal));
    }

    /// <summary>変換元の表。</summary>
    public TableInfo Source { get; }

    /// <summary>変換先の表。変換先に無いときは <see langword="null"/>。</summary>
    public TableInfo? Target { get; }

    /// <summary>表名（変換元の名前）。</summary>
    public string Name => Source.Name;

    /// <summary>変換先に表が無い（変換元だけにある）か。</summary>
    public bool IsSourceOnly => Target is null;

    /// <summary>コピーする列の対応（変換元の読み取り順 = 変換先の挿入順）。</summary>
    public IReadOnlyList<ColumnMapping> Mappings { get; }

    /// <summary>警告（英語）。</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>この表を変換できない理由（英語）。あれば表ごと失敗にして、何も書かない。</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>変換元の主キーの列の位置（<see cref="Mappings"/> の添字）。主キーの列が揃わないときは空。</summary>
    public IReadOnlyList<int> SourceKeyIndexes { get; }

    /// <summary>変換先の主キーの列の位置（<see cref="Mappings"/> の添字）。主キーの列が揃わないときは空。</summary>
    public IReadOnlyList<int> TargetKeyIndexes { get; }

    /// <summary>変換先の自動採番列。無ければ <see langword="null"/>。</summary>
    public ColumnInfo? AutoIncrement { get; }

    /// <summary>自動採番列の値を変換元からコピーするか（変換元に同名の列があるか）。</summary>
    public bool AutoIncrementIsMapped { get; }

    /// <summary>エラーが無く、コピーできる表か。</summary>
    public bool IsRunnable => Errors.Count == 0 && Target is not null;

    private static IReadOnlyList<int> ResolveKeyIndexes(IReadOnlyList<string> key, IReadOnlyList<string> mapped)
    {
        if (key.Count == 0)
        {
            return [];
        }

        var indexes = new List<int>();
        foreach (var name in key)
        {
            var index = -1;
            for (var i = 0; i < mapped.Count; i++)
            {
                if (string.Equals(mapped[i], name, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                return [];
            }

            indexes.Add(index);
        }

        return indexes;
    }
}

/// <summary>変換計画。</summary>
public sealed class ConversionPlan
{
    public ConversionPlan(
        IReadOnlyList<TablePlan> tables,
        IReadOnlyList<string> targetOnlyTables,
        IReadOnlyList<string> excludedTables,
        IReadOnlyList<string> errors)
    {
        Tables = tables;
        TargetOnlyTables = targetOnlyTables;
        ExcludedTables = excludedTables;
        Errors = errors;
    }

    /// <summary>コピーする順の表（親 → 子）。変換先に無い表は最後。</summary>
    public IReadOnlyList<TablePlan> Tables { get; }

    /// <summary>変換先だけにある表（コピーする物が無いので何もしない）。</summary>
    public IReadOnlyList<string> TargetOnlyTables { get; }

    /// <summary>対象から外した表と理由（英語）。</summary>
    public IReadOnlyList<string> ExcludedTables { get; }

    /// <summary>計画全体のエラー（<c>--tables</c> に存在しない表を指定した、など。英語）。</summary>
    public IReadOnlyList<string> Errors { get; }
}

/// <summary>計画を立てるときの指定。</summary>
public sealed class PlanOptions
{
    /// <summary>変換する表。空なら全部。</summary>
    public IReadOnlyList<string> Tables { get; init; } = [];

    /// <summary>変換しない表。</summary>
    public IReadOnlyList<string> ExcludeTables { get; init; } = [];

    /// <summary>外部キーの循環があるときに、制約を一時的に無効にして続けるか。</summary>
    public bool DisableConstraints { get; init; }

    /// <summary>このツールの管理表の名前（対象から外す）。</summary>
    public string ProgressTableName { get; init; } = DatabaseConverterConstants.ProgressTableName;
}

/// <summary>定数。</summary>
public static class DatabaseConverterConstants
{
    /// <summary>変換先に作る管理表（表ごとの完了の記録）。Pleasanter の表と衝突しない名前。</summary>
    public const string ProgressTableName = "__DatabaseConverterProgress";

    /// <summary>既定でコピーしない表の接頭辞（Quartz のスケジューラ状態。CodeDefiner の migrate も除外している）。</summary>
    public const string QuartzTablePrefix = "QRTZ_";
}

/// <summary>
/// 変換元と変換先のスキーマ（情報スキーマ）を突き合わせて、変換計画を立てる。
/// Pleasanter の表の定義は持たない。同名の表・列を突き合わせるだけ。
/// </summary>
public static class ConversionPlanner
{
    public static ConversionPlan Plan(SchemaInfo source, SchemaInfo target, PlanOptions options)
    {
        var errors = new List<string>();
        var excluded = new List<string>();
        var include = options.Tables.Count == 0 ? null : new HashSet<string>(options.Tables, StringComparer.OrdinalIgnoreCase);
        var exclude = new HashSet<string>(options.ExcludeTables, StringComparer.OrdinalIgnoreCase);

        if (include is not null)
        {
            foreach (var name in options.Tables)
            {
                if (source.FindTable(name) is null && target.FindTable(name) is null)
                {
                    errors.Add($"table '{name}' given by --tables exists in neither the source nor the target");
                }
            }
        }

        foreach (var name in options.ExcludeTables)
        {
            if (source.FindTable(name) is null && target.FindTable(name) is null)
            {
                errors.Add($"table '{name}' given by --exclude-tables exists in neither the source nor the target");
            }
        }

        var plans = new List<TablePlan>();
        foreach (var table in source.Tables.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(table.Name, options.ProgressTableName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var named = include?.Contains(table.Name) == true;
            if (include is not null && !named)
            {
                continue;
            }

            if (exclude.Contains(table.Name))
            {
                excluded.Add($"{table.Name}: excluded by --exclude-tables");
                continue;
            }

            if (!named && table.Name.StartsWith(DatabaseConverterConstants.QuartzTablePrefix, StringComparison.OrdinalIgnoreCase))
            {
                excluded.Add($"{table.Name}: Quartz scheduler table (not copied by default; name it with --tables to copy)");
                continue;
            }

            plans.Add(PlanTable(table, target.FindTable(table.Name)));
        }

        var targetOnly = target.Tables
            .Where(t => !string.Equals(t.Name, options.ProgressTableName, StringComparison.OrdinalIgnoreCase))
            .Where(t => source.FindTable(t.Name) is null)
            .Where(t => include is null || include.Contains(t.Name))
            .Where(t => !exclude.Contains(t.Name))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var ordered = OrderByForeignKeys(plans, target, options.DisableConstraints);
        return new ConversionPlan(ordered, targetOnly, excluded, errors);
    }

    private static TablePlan PlanTable(TableInfo source, TableInfo? target)
    {
        if (target is null)
        {
            return new TablePlan(source, null, [], [], []);
        }

        var mappings = new List<ColumnMapping>();
        var warnings = new List<string>();
        var errors = new List<string>();
        var matchedTargets = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sourceColumn in source.Columns)
        {
            if (sourceColumn.IsReadOnly)
            {
                warnings.Add($"source column '{sourceColumn.Name}' ({sourceColumn.Type.Describe()}) is computed or a row version; it is not copied");
                continue;
            }

            var targetColumn = target.FindColumn(sourceColumn.Name);
            if (targetColumn is null)
            {
                warnings.Add($"source column '{sourceColumn.Name}' does not exist in the target table; its values are ignored");
                continue;
            }

            if (targetColumn.IsReadOnly)
            {
                warnings.Add($"target column '{targetColumn.Name}' is computed or a row version; the source values are ignored");
                continue;
            }

            matchedTargets.Add(targetColumn.Name);
            var unsupported = ValueConverter.CheckSupported(sourceColumn.Type, targetColumn.Type);
            if (unsupported is not null)
            {
                errors.Add($"column '{sourceColumn.Name}': {unsupported}");
                continue;
            }

            var risk = ValueConverter.DescribeRisk(sourceColumn.Type, targetColumn.Type);
            if (risk is not null)
            {
                warnings.Add($"column '{sourceColumn.Name}': {risk}");
            }

            mappings.Add(new ColumnMapping(sourceColumn, targetColumn, risk));
        }

        foreach (var targetColumn in target.Columns)
        {
            if (targetColumn.IsReadOnly || matchedTargets.Contains(targetColumn.Name))
            {
                continue;
            }

            if (targetColumn.IsNullable || targetColumn.HasDefault || targetColumn.IsAutoIncrement)
            {
                warnings.Add($"target column '{targetColumn.Name}' does not exist in the source table; the database default or NULL is used");
            }
            else
            {
                errors.Add($"target column '{targetColumn.Name}' is NOT NULL without a default and does not exist in the source table");
            }
        }

        if (mappings.Count == 0 && errors.Count == 0)
        {
            errors.Add("no columns in common between the source and target tables");
        }

        return new TablePlan(source, target, mappings, warnings, errors);
    }

    /// <summary>外部キーの親 → 子の順に並べる。同順位は表名順。循環する表は、制約を無効にしない限りエラー。</summary>
    private static List<TablePlan> OrderByForeignKeys(List<TablePlan> plans, SchemaInfo target, bool disableConstraints)
    {
        var runnable = plans.Where(p => p.Target is not null).ToList();
        var sourceOnly = plans.Where(p => p.Target is null).ToList();
        var byName = runnable.ToDictionary(p => p.Target!.Name, StringComparer.Ordinal);

        var dependencies = new Dictionary<TablePlan, HashSet<TablePlan>>();
        foreach (var plan in runnable)
        {
            var set = new HashSet<TablePlan>();
            foreach (var parentName in plan.Target!.ReferencedTables)
            {
                var parent = target.FindTable(parentName);
                if (parent is not null && byName.TryGetValue(parent.Name, out var parentPlan))
                {
                    set.Add(parentPlan);
                }
            }

            dependencies[plan] = set;
        }

        var ordered = new List<TablePlan>();
        var remaining = runnable.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var placed = new HashSet<TablePlan>();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(p => dependencies[p].All(d => placed.Contains(d))).ToList();
            if (ready.Count == 0)
            {
                // 循環（自己参照を含む）。残りを表名順に置く
                foreach (var plan in remaining)
                {
                    var tail = disableConstraints
                        ? plan
                        : WithError(plan, "circular or self-referencing foreign key; run with --disable-constraints to copy this table");
                    ordered.Add(tail);
                }

                break;
            }

            foreach (var plan in ready)
            {
                ordered.Add(dependencies[plan].Contains(plan) && !disableConstraints
                    ? WithError(plan, "self-referencing foreign key; run with --disable-constraints to copy this table")
                    : plan);
                placed.Add(plan);
                remaining.Remove(plan);
            }
        }

        ordered.AddRange(sourceOnly);
        return ordered;
    }

    private static TablePlan WithError(TablePlan plan, string error)
        => new(plan.Source, plan.Target, plan.Mappings, plan.Warnings, [.. plan.Errors, error]);
}
