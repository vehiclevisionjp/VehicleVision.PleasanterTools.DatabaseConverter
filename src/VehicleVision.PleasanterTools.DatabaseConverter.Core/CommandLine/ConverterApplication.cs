using VehicleVision.PleasanterTools.DatabaseConverter.Core.Databases;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Engine;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Planning;

namespace VehicleVision.PleasanterTools.DatabaseConverter.Core.CommandLine;

/// <summary>
/// コマンドラインの本体。入出力・環境変数・DB の接続を差し替えられるので、DB なしでテストできる。
/// コンソールへ出す文字列は英語（Azure の Kudu の Debug console で日本語が化けるため）。
/// </summary>
public sealed class ConverterApplication
{
    /// <summary>終了コード: 全件成功。</summary>
    public const int ExitSuccess = 0;

    /// <summary>終了コード: 失敗があった。</summary>
    public const int ExitFailure = 1;

    /// <summary>終了コード: 引数エラー。</summary>
    public const int ExitUsage = 2;

    private readonly IDatabaseOpener _opener;
    private readonly TextReader _stdin;
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly string _version;

    public ConverterApplication(
        IDatabaseOpener opener,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        Func<string, string?> getEnvironmentVariable,
        string version)
    {
        _opener = opener;
        _stdin = stdin;
        _stdout = stdout;
        _stderr = stderr;
        _getEnvironmentVariable = getEnvironmentVariable;
        _version = version;
    }

    /// <summary>実行して終了コードを返す。</summary>
    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var parsed = ArgumentParser.Parse(args);
        switch (parsed.Kind)
        {
            case ParseOutcomeKind.Help:
                _stdout.WriteLine(ArgumentParser.HelpText);
                return ExitSuccess;
            case ParseOutcomeKind.Version:
                _stdout.WriteLine($"database-converter {_version}");
                return ExitSuccess;
            case ParseOutcomeKind.Error:
                _stderr.WriteLine($"ERROR: {parsed.Error}");
                _stderr.WriteLine("Run 'database-converter --help' for usage.");
                return ExitUsage;
        }

        var command = parsed.Command!;
        var log = new ConsoleConversionLog(_stdout, _stderr);

        var sourceText = ResolveConnectionString(command.SourceConnection, "source", out var sourceError);
        var targetText = sourceError is null
            ? ResolveConnectionString(command.TargetConnection, "target", out sourceError)
            : null;
        if (sourceError is not null || sourceText is null || targetText is null)
        {
            _stderr.WriteLine($"ERROR: {sourceError}");
            return ExitUsage;
        }

        SecretRedactor.RegisterConnectionString(sourceText);
        SecretRedactor.RegisterConnectionString(targetText);

        log.Info($"Converting {command.From.ToDisplayName()} -> {command.To.ToDisplayName()}{(command.DryRun ? " (dry-run: nothing will be written)" : string.Empty)}");

        ISourceDatabase? source = null;
        ITargetDatabase? target = null;
        try
        {
            try
            {
                source = await _opener.OpenSourceAsync(command.From, sourceText, command.CommandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Error($"Could not open the source database: {ConversionEngine.Describe(ex)}");
                return ExitFailure;
            }

            try
            {
                target = await _opener.OpenTargetAsync(command.To, targetText, command.CommandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Error($"Could not open the target database: {ConversionEngine.Describe(ex)}");
                return ExitFailure;
            }

            Schema.SchemaInfo sourceSchema;
            Schema.SchemaInfo targetSchema;
            try
            {
                sourceSchema = await source.ReadSchemaAsync(cancellationToken).ConfigureAwait(false);
                targetSchema = await target.ReadSchemaAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Error($"Could not read the database schema: {ConversionEngine.Describe(ex)}");
                return ExitFailure;
            }

            foreach (var (side, dbms, schema) in new[] { ("source", command.From, sourceSchema), ("target", command.To, targetSchema) })
            {
                if (schema.Tables.Count == 0)
                {
                    var hint = dbms == Dbms.PostgreSql
                        ? " Pleasanter on PostgreSQL keeps its tables in a schema named after the service; add 'Search Path=<service name>' to the connection string."
                        : string.Empty;
                    log.Error($"No tables were found in the current schema of the {side} database.{hint}");
                    return ExitFailure;
                }
            }

            var plan = ConversionPlanner.Plan(sourceSchema, targetSchema, new PlanOptions
            {
                Tables = command.Tables,
                ExcludeTables = command.ExcludeTables,
                DisableConstraints = command.DisableConstraints,
            });
            log.Info($"Source tables: {sourceSchema.Tables.Count}, target tables: {targetSchema.Tables.Count}, tables to process: {plan.Tables.Count}");

            var engine = new ConversionEngine(source, target, log);
            var report = await engine.RunAsync(
                plan,
                new RunOptions
                {
                    DryRun = command.DryRun,
                    Resume = command.Resume,
                    BatchSize = command.BatchSize,
                    MaxBatchBytes = command.MaxBatchBytes,
                    DisableConstraints = command.DisableConstraints,
                    CleanupProgress = command.CleanupProgress,
                },
                cancellationToken).ConfigureAwait(false);
            report.WriteSummary(log);
            return report.ExitCode;
        }
        catch (OperationCanceledException)
        {
            log.Error("Interrupted.");
            return ExitFailure;
        }
        finally
        {
            if (target is not null)
            {
                await target.DisposeAsync().ConfigureAwait(false);
            }

            if (source is not null)
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private string? ResolveConnectionString(ConnectionSpec spec, string side, out string? error)
    {
        error = null;
        string? text;
        switch (spec.Kind)
        {
            case ConnectionSourceKind.Environment:
                text = _getEnvironmentVariable(spec.Value!);
                if (string.IsNullOrWhiteSpace(text))
                {
                    error = $"the environment variable '{spec.Value}' holding the {side} connection string is not set or is empty";
                    return null;
                }

                return text.Trim();
            case ConnectionSourceKind.File:
                try
                {
                    text = File.ReadLines(spec.Value!).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    error = $"could not read the {side} connection string file '{spec.Value}' ({ex.GetType().Name})";
                    return null;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    error = $"the {side} connection string file '{spec.Value}' is empty";
                    return null;
                }

                return text.Trim();
            default:
                string? line;
                do
                {
                    line = _stdin.ReadLine();
                }
                while (line is not null && string.IsNullOrWhiteSpace(line));
                if (line is null)
                {
                    error = $"no {side} connection string was given on standard input";
                    return null;
                }

                return line.Trim();
        }
    }
}
