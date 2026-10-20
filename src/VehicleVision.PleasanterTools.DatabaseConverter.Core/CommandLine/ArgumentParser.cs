using System.Globalization;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Engine;

namespace VehicleVision.PleasanterTools.DatabaseConverter.Core.CommandLine;

/// <summary>接続文字列の受け取り方。パスワードを引数へ直書きさせないため、環境変数・ファイル・標準入力だけ。</summary>
public enum ConnectionSourceKind
{
    Environment,
    File,
    Stdin,
}

/// <summary>接続文字列の出どころ。</summary>
/// <param name="Kind">受け取り方。</param>
/// <param name="Value">環境変数名、またはファイルのパス。標準入力のときは <see langword="null"/>。</param>
public sealed record ConnectionSpec(ConnectionSourceKind Kind, string? Value);

/// <summary><c>convert</c> コマンドの指定。</summary>
public sealed class ConvertCommand
{
    public const string DefaultSourceEnvironmentVariable = "DBCONV_SOURCE_CONNECTION";

    public const string DefaultTargetEnvironmentVariable = "DBCONV_TARGET_CONNECTION";

    public required Dbms From { get; init; }

    public required Dbms To { get; init; }

    public required ConnectionSpec SourceConnection { get; init; }

    public required ConnectionSpec TargetConnection { get; init; }

    public bool DryRun { get; init; }

    public bool Resume { get; init; }

    public bool DisableConstraints { get; init; }

    public bool CleanupProgress { get; init; }

    public int BatchSize { get; init; } = RunOptions.DefaultBatchSize;

    public long MaxBatchBytes { get; init; } = RunOptions.DefaultMaxBatchBytes;

    public int CommandTimeoutSeconds { get; init; } = 600;

    public IReadOnlyList<string> Tables { get; init; } = [];

    public IReadOnlyList<string> ExcludeTables { get; init; } = [];
}

/// <summary>解析の結果の種類。</summary>
public enum ParseOutcomeKind
{
    Run,
    Help,
    Version,
    Error,
}

/// <summary>引数の解析結果。</summary>
public sealed class ParseOutcome
{
    private ParseOutcome(ParseOutcomeKind kind, ConvertCommand? command, string? error)
    {
        Kind = kind;
        Command = command;
        Error = error;
    }

    public ParseOutcomeKind Kind { get; }

    public ConvertCommand? Command { get; }

    /// <summary>引数のエラー（英語）。</summary>
    public string? Error { get; }

    public static ParseOutcome Run(ConvertCommand command) => new(ParseOutcomeKind.Run, command, null);

    public static ParseOutcome Help() => new(ParseOutcomeKind.Help, null, null);

    public static ParseOutcome Version() => new(ParseOutcomeKind.Version, null, null);

    public static ParseOutcome Fail(string error) => new(ParseOutcomeKind.Error, null, error);
}

/// <summary>引数の解析（ライブラリを使わない素直な実装）。</summary>
public static class ArgumentParser
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
    {
        "--dry-run",
        "--resume",
        "--disable-constraints",
        "--cleanup-progress",
        "--source-connection-stdin",
        "--target-connection-stdin",
        "--help",
        "-h",
    };

    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "--from",
        "--to",
        "--source-connection-env",
        "--source-connection-file",
        "--target-connection-env",
        "--target-connection-file",
        "--batch-size",
        "--max-batch-bytes",
        "--command-timeout",
        "--tables",
        "--exclude-tables",
    };

    /// <summary>引数を解析する。</summary>
    public static ParseOutcome Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return ParseOutcome.Fail("no command given");
        }

        var first = args[0];
        if (first is "--help" or "-h" or "help")
        {
            return ParseOutcome.Help();
        }

        if (first is "--version" or "version")
        {
            return ParseOutcome.Version();
        }

        if (first != "convert")
        {
            return ParseOutcome.Fail($"unknown command '{first}' (the only command is 'convert')");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            string? inlineValue = null;
            var name = arg;
            var equals = arg.IndexOf('=', StringComparison.Ordinal);
            if (arg.StartsWith("--", StringComparison.Ordinal) && equals > 0)
            {
                name = arg[..equals];
                inlineValue = arg[(equals + 1)..];
            }

            if (Flags.Contains(name))
            {
                if (inlineValue is not null)
                {
                    return ParseOutcome.Fail($"option '{name}' does not take a value");
                }

                if (name is "--help" or "-h")
                {
                    return ParseOutcome.Help();
                }

                if (!flags.Add(name))
                {
                    return ParseOutcome.Fail($"option '{name}' was given more than once");
                }
            }
            else if (ValueOptions.Contains(name))
            {
                string value;
                if (inlineValue is not null)
                {
                    value = inlineValue;
                }
                else
                {
                    if (i + 1 >= args.Count)
                    {
                        return ParseOutcome.Fail($"option '{name}' requires a value");
                    }

                    value = args[++i];
                }

                if (!values.TryAdd(name, value))
                {
                    return ParseOutcome.Fail($"option '{name}' was given more than once");
                }
            }
            else
            {
                return ParseOutcome.Fail($"unknown option '{arg}'");
            }
        }

        if (!values.TryGetValue("--from", out var fromText))
        {
            return ParseOutcome.Fail("option '--from' is required");
        }

        if (!values.TryGetValue("--to", out var toText))
        {
            return ParseOutcome.Fail("option '--to' is required");
        }

        if (!DbmsNames.TryParse(fromText, out var from))
        {
            return ParseOutcome.Fail($"unknown DBMS '{fromText}' for '--from' (use sqlserver, postgresql or mysql)");
        }

        if (!DbmsNames.TryParse(toText, out var to))
        {
            return ParseOutcome.Fail($"unknown DBMS '{toText}' for '--to' (use sqlserver, postgresql or mysql)");
        }

        if (from == to)
        {
            return ParseOutcome.Fail($"the source and target DBMS must differ (both are {from.ToDisplayName()}); this tool converts between different DBMSs");
        }

        var source = ResolveSpec("source", values, flags, ConvertCommand.DefaultSourceEnvironmentVariable, out var sourceError);
        if (source is null)
        {
            return ParseOutcome.Fail(sourceError!);
        }

        var target = ResolveSpec("target", values, flags, ConvertCommand.DefaultTargetEnvironmentVariable, out var targetError);
        if (target is null)
        {
            return ParseOutcome.Fail(targetError!);
        }

        if (source.Kind == ConnectionSourceKind.Stdin && target.Kind == ConnectionSourceKind.Stdin)
        {
            return ParseOutcome.Fail("only one of the source and target connection strings can be read from standard input");
        }

        var batchSize = RunOptions.DefaultBatchSize;
        if (values.TryGetValue("--batch-size", out var batchText)
            && (!int.TryParse(batchText, NumberStyles.None, CultureInfo.InvariantCulture, out batchSize) || batchSize is < 1 or > 100_000))
        {
            return ParseOutcome.Fail("option '--batch-size' must be an integer from 1 to 100000");
        }

        var maxBytes = RunOptions.DefaultMaxBatchBytes;
        if (values.TryGetValue("--max-batch-bytes", out var bytesText)
            && (!long.TryParse(bytesText, NumberStyles.None, CultureInfo.InvariantCulture, out maxBytes) || maxBytes is < 1024 or > 1_073_741_824))
        {
            return ParseOutcome.Fail("option '--max-batch-bytes' must be an integer from 1024 to 1073741824");
        }

        var timeout = 600;
        if (values.TryGetValue("--command-timeout", out var timeoutText)
            && (!int.TryParse(timeoutText, NumberStyles.None, CultureInfo.InvariantCulture, out timeout) || timeout > 86_400))
        {
            return ParseOutcome.Fail("option '--command-timeout' must be an integer from 0 (no limit) to 86400 seconds");
        }

        if (!TryParseList(values, "--tables", out var tables, out var listError)
            || !TryParseList(values, "--exclude-tables", out var excluded, out listError))
        {
            return ParseOutcome.Fail(listError!);
        }

        return ParseOutcome.Run(new ConvertCommand
        {
            From = from,
            To = to,
            SourceConnection = source,
            TargetConnection = target,
            DryRun = flags.Contains("--dry-run"),
            Resume = flags.Contains("--resume"),
            DisableConstraints = flags.Contains("--disable-constraints"),
            CleanupProgress = flags.Contains("--cleanup-progress"),
            BatchSize = batchSize,
            MaxBatchBytes = maxBytes,
            CommandTimeoutSeconds = timeout,
            Tables = tables,
            ExcludeTables = excluded,
        });
    }

    private static ConnectionSpec? ResolveSpec(
        string side,
        Dictionary<string, string> values,
        HashSet<string> flags,
        string defaultEnvironmentVariable,
        out string? error)
    {
        error = null;
        var hasEnv = values.TryGetValue($"--{side}-connection-env", out var env);
        var hasFile = values.TryGetValue($"--{side}-connection-file", out var file);
        var hasStdin = flags.Contains($"--{side}-connection-stdin");
        if ((hasEnv ? 1 : 0) + (hasFile ? 1 : 0) + (hasStdin ? 1 : 0) > 1)
        {
            error = $"specify only one of --{side}-connection-env, --{side}-connection-file and --{side}-connection-stdin";
            return null;
        }

        if (hasEnv)
        {
            if (string.IsNullOrWhiteSpace(env))
            {
                error = $"option '--{side}-connection-env' requires an environment variable name";
                return null;
            }

            return new ConnectionSpec(ConnectionSourceKind.Environment, env);
        }

        if (hasFile)
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                error = $"option '--{side}-connection-file' requires a file path";
                return null;
            }

            return new ConnectionSpec(ConnectionSourceKind.File, file);
        }

        return hasStdin
            ? new ConnectionSpec(ConnectionSourceKind.Stdin, null)
            : new ConnectionSpec(ConnectionSourceKind.Environment, defaultEnvironmentVariable);
    }

    private static bool TryParseList(Dictionary<string, string> values, string name, out IReadOnlyList<string> list, out string? error)
    {
        error = null;
        list = [];
        if (!values.TryGetValue(name, out var text))
        {
            return true;
        }

        var items = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (items.Length == 0)
        {
            error = $"option '{name}' requires a comma-separated list of table names";
            return false;
        }

        list = items.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return true;
    }

    /// <summary>使い方（英語）。</summary>
    public static string HelpText => """
        database-converter - converts a Pleasanter database between SQL Server, PostgreSQL and MySQL.

        Usage:
          database-converter convert --from <dbms> --to <dbms> [options]
          database-converter --help | --version

        The source database is only read. The target must be an EMPTY database whose tables were
        created by the Pleasanter CodeDefiner. Run with --dry-run first, and back up both databases.

        Required:
          --from <dbms>                      source DBMS: sqlserver | postgresql | mysql
          --to <dbms>                        target DBMS (must differ from --from)

        Connection strings (never pass them as arguments; use one of these for each side):
          --source-connection-env <NAME>     environment variable holding the source connection string
                                             (default: DBCONV_SOURCE_CONNECTION)
          --source-connection-file <PATH>    file whose first non-empty line is the source connection string
          --source-connection-stdin          read the source connection string from standard input
          --target-connection-env <NAME>     (default: DBCONV_TARGET_CONNECTION)
          --target-connection-file <PATH>
          --target-connection-stdin          (only one side can use standard input)

        Options:
          --dry-run                          show what would be copied; write nothing
          --resume                           skip tables recorded as completed; reload unfinished tables from the start
          --tables <A,B,...>                 copy only these tables
          --exclude-tables <A,B,...>         do not copy these tables
          --batch-size <N>                   rows per batch (default 1000, 1-100000)
          --max-batch-bytes <N>              also flush a batch at about N bytes (default 4194304)
          --command-timeout <SECONDS>        per-command timeout (default 600; 0 = no limit)
          --disable-constraints              disable foreign keys on the target while copying
          --cleanup-progress                 drop the progress table (__DatabaseConverterProgress) after a fully successful run
          --help, -h                         show this help
          --version                          show the version

        Exit codes: 0 = everything succeeded, 1 = failures occurred, 2 = invalid arguments.
        """;
}
