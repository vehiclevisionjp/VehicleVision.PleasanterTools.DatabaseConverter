using VehicleVision.PleasanterTools.DatabaseConverter.Core.CommandLine;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Databases;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Engine;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Schema;

namespace VehicleVision.PleasanterTools.DatabaseConverter.Core.Tests;

public sealed class ArgumentParserTests
{
    private static ParseOutcome Parse(params string[] args) => ArgumentParser.Parse(args);

    private static ConvertCommand Ok(params string[] args)
    {
        var outcome = Parse(args);
        Assert.True(outcome.Kind == ParseOutcomeKind.Run, outcome.Error);
        return outcome.Command!;
    }

    [Fact]
    public void 最小の指定は既定の環境変数から接続文字列を読む()
    {
        var command = Ok("convert", "--from", "sqlserver", "--to", "postgresql");
        Assert.Equal(Dbms.SqlServer, command.From);
        Assert.Equal(Dbms.PostgreSql, command.To);
        Assert.Equal(new ConnectionSpec(ConnectionSourceKind.Environment, "DBCONV_SOURCE_CONNECTION"), command.SourceConnection);
        Assert.Equal(new ConnectionSpec(ConnectionSourceKind.Environment, "DBCONV_TARGET_CONNECTION"), command.TargetConnection);
        Assert.False(command.DryRun);
        Assert.False(command.Resume);
        Assert.Equal(1000, command.BatchSize);
        Assert.Empty(command.Tables);
    }

    [Fact]
    public void すべてのオプションを解析する()
    {
        var command = Ok(
            "convert", "--from=mysql", "--to=sqlserver", "--dry-run", "--resume", "--batch-size", "50",
            "--tables", "Items, Results", "--exclude-tables=Binaries", "--disable-constraints", "--cleanup-progress",
            "--source-connection-env", "SRC", "--target-connection-file", "t.txt",
            "--max-batch-bytes", "2048", "--command-timeout", "0");
        Assert.True(command.DryRun);
        Assert.True(command.Resume);
        Assert.True(command.DisableConstraints);
        Assert.True(command.CleanupProgress);
        Assert.Equal(50, command.BatchSize);
        Assert.Equal(["Items", "Results"], command.Tables);
        Assert.Equal(["Binaries"], command.ExcludeTables);
        Assert.Equal(new ConnectionSpec(ConnectionSourceKind.Environment, "SRC"), command.SourceConnection);
        Assert.Equal(new ConnectionSpec(ConnectionSourceKind.File, "t.txt"), command.TargetConnection);
        Assert.Equal(2048, command.MaxBatchBytes);
        Assert.Equal(0, command.CommandTimeoutSeconds);
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("postgresql")]
    [InlineData("mysql")]
    public void 同じDBMS同士は引数エラー(string dbms)
    {
        var outcome = Parse("convert", "--from", dbms, "--to", dbms.ToUpperInvariant());
        Assert.Equal(ParseOutcomeKind.Error, outcome.Kind);
        Assert.Contains("must differ", outcome.Error);
    }

    [Theory]
    [InlineData("sqlserver", "postgresql")]
    [InlineData("sqlserver", "mysql")]
    [InlineData("postgresql", "sqlserver")]
    [InlineData("postgresql", "mysql")]
    [InlineData("mysql", "sqlserver")]
    [InlineData("mysql", "postgresql")]
    public void 全6通りの向きはすべて受け付ける(string from, string to)
        => Assert.Equal(ParseOutcomeKind.Run, Parse("convert", "--from", from, "--to", to).Kind);

    [Theory]
    [InlineData("convert", "--to", "mysql")]
    [InlineData("convert", "--from", "mysql")]
    [InlineData("convert", "--from", "oracle", "--to", "mysql")]
    [InlineData("convert", "--from", "mysql", "--to", "sqlserver", "--bogus")]
    [InlineData("convert", "--from", "mysql", "--to", "sqlserver", "--batch-size", "0")]
    [InlineData("convert", "--from", "mysql", "--to", "sqlserver", "--batch-size", "100001")]
    [InlineData("convert", "--from", "mysql", "--to", "sqlserver", "--batch-size", "abc")]
    [InlineData("convert", "--from", "mysql", "--to", "sqlserver", "--batch-size")]
    [InlineData("convert", "--from", "mysql", "--to", "sqlserver", "--dry-run", "--dry-run")]
    [InlineData("convert", "--from", "mysql", "--to", "sqlserver", "--from", "mysql")]
    [InlineData("convert", "--from", "mysql", "--to", "sqlserver", "--dry-run=true")]
    [InlineData("convert", "--from", "mysql", "--to", "sqlserver", "--tables", ",")]
    [InlineData("convert", "--from", "mysql", "--to", "sqlserver", "--source-connection-env", "A", "--source-connection-file", "f")]
    [InlineData("convert", "--from", "mysql", "--to", "sqlserver", "--source-connection-stdin", "--target-connection-stdin")]
    [InlineData("convert", "--from", "mysql", "--to", "sqlserver", "--command-timeout", "-1")]
    [InlineData("convert", "--from", "mysql", "--to", "sqlserver", "--max-batch-bytes", "10")]
    [InlineData("frobnicate")]
    [InlineData]
    public void 不正な引数はエラー(params string[] args)
        => Assert.Equal(ParseOutcomeKind.Error, Parse(args).Kind);

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    public void ヘルプ(string flag)
        => Assert.Equal(ParseOutcomeKind.Help, Parse(flag).Kind);

    [Fact]
    public void convertの後ろのヘルプも受け付ける()
        => Assert.Equal(ParseOutcomeKind.Help, Parse("convert", "--help").Kind);

    [Fact]
    public void 接続文字列を引数に直接書くオプションは無い()
    {
        Assert.Equal(ParseOutcomeKind.Error, Parse("convert", "--from", "mysql", "--to", "sqlserver", "--source-connection", "Server=x;Password=y").Kind);
        Assert.Equal(ParseOutcomeKind.Error, Parse("convert", "--from", "mysql", "--to", "sqlserver", "--password", "y").Kind);
    }

    [Fact]
    public void ヘルプの文面は英語で_終了コードと環境変数を説明する()
    {
        var help = ArgumentParser.HelpText;
        Assert.Contains("DBCONV_SOURCE_CONNECTION", help);
        Assert.Contains("--dry-run", help);
        Assert.Contains("Exit codes", help);
        Assert.All(help, c => Assert.True(c < 128, $"non-ASCII character in help: {c}"));
    }
}

[Collection("SecretRedactor")]
public sealed class ConverterApplicationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dbconv-test-" + Guid.NewGuid().ToString("N"));

    public ConverterApplicationTests()
    {
        Directory.CreateDirectory(_directory);
        SecretRedactor.Clear();
    }

    public void Dispose()
    {
        SecretRedactor.Clear();
        Directory.Delete(_directory, true);
    }

    private sealed class FakeOpener : IDatabaseOpener
    {
        public FakeOpener(InMemoryDatabase source, InMemoryDatabase target)
        {
            Source = source;
            Target = target;
        }

        public InMemoryDatabase Source { get; }

        public InMemoryDatabase Target { get; }

        public List<string> ConnectionStrings { get; } = [];

        public Exception? FailOnOpenSource { get; set; }

        public Task<ISourceDatabase> OpenSourceAsync(Dbms dbms, string connectionString, int commandTimeoutSeconds, CancellationToken cancellationToken)
        {
            ConnectionStrings.Add(connectionString);
            return FailOnOpenSource is null ? Task.FromResult<ISourceDatabase>(Source) : Task.FromException<ISourceDatabase>(FailOnOpenSource);
        }

        public Task<ITargetDatabase> OpenTargetAsync(Dbms dbms, string connectionString, int commandTimeoutSeconds, CancellationToken cancellationToken)
        {
            ConnectionStrings.Add(connectionString);
            return Task.FromResult<ITargetDatabase>(Target);
        }
    }

    private static (InMemoryDatabase Source, InMemoryDatabase Target) Databases()
    {
        var table = T.Table("Items", [T.Col("Id", T.BigInt, nullable: false), T.Col("Title", T.Text)], pk: ["Id"]);
        var source = new InMemoryDatabase(Dbms.SqlServer).Add(table, new object?[] { 1L, "a" }, new object?[] { 2L, "b" });
        var target = new InMemoryDatabase(Dbms.PostgreSql).Add(table);
        return (source, target);
    }

    private (int Code, string Out, string Err, FakeOpener Opener) Run(
        string[] args, Dictionary<string, string?>? env = null, string stdin = "", FakeOpener? opener = null)
    {
        var (source, target) = Databases();
        opener ??= new FakeOpener(source, target);
        var output = new StringWriter();
        var error = new StringWriter();
        var app = new ConverterApplication(
            opener,
            new StringReader(stdin),
            output,
            error,
            name => env is not null && env.TryGetValue(name, out var v) ? v : null,
            "1.2.3");
        var code = app.RunAsync(args, CancellationToken.None).GetAwaiter().GetResult();
        return (code, output.ToString(), error.ToString(), opener);
    }

    private static readonly Dictionary<string, string?> DefaultEnvironment = new()
    {
        ["DBCONV_SOURCE_CONNECTION"] = "Server=src;User Id=sa;Password=SourceSecret!;",
        ["DBCONV_TARGET_CONNECTION"] = "Host=dst;Username=u;Password=TargetSecret!",
    };

    [Fact]
    public void 全件成功なら終了コードは0()
    {
        var result = Run(["convert", "--from", "sqlserver", "--to", "postgresql"], DefaultEnvironment);
        Assert.Equal(0, result.Code);
        Assert.Contains("Result: OK", result.Out);
        Assert.Equal(2, result.Opener.Target.Rows("Items").Count);
    }

    [Fact]
    public void 失敗があれば終了コードは1()
    {
        var (source, target) = Databases();
        target.RejectRow = (_, row) => (long)row[0]! == 2 ? "rejected" : null;
        var result = Run(["convert", "--from", "sqlserver", "--to", "postgresql"], DefaultEnvironment, opener: new FakeOpener(source, target));
        Assert.Equal(1, result.Code);
        Assert.Contains("Result: FAILED", result.Err);
    }

    [Fact]
    public void 引数エラーは終了コード2で_使い方を案内する()
    {
        var result = Run(["convert", "--from", "mysql", "--to", "mysql"], DefaultEnvironment);
        Assert.Equal(2, result.Code);
        Assert.Contains("must differ", result.Err);
        Assert.Contains("--help", result.Err);
        Assert.Empty(result.Opener.ConnectionStrings);
    }

    [Fact]
    public void ヘルプとバージョンは終了コード0()
    {
        var help = Run(["--help"]);
        Assert.Equal(0, help.Code);
        Assert.Contains("Usage:", help.Out);
        var version = Run(["--version"]);
        Assert.Equal(0, version.Code);
        Assert.Contains("1.2.3", version.Out);
    }

    [Fact]
    public void 環境変数が無ければ終了コード2()
    {
        var result = Run(["convert", "--from", "sqlserver", "--to", "postgresql"], new Dictionary<string, string?>());
        Assert.Equal(2, result.Code);
        Assert.Contains("DBCONV_SOURCE_CONNECTION", result.Err);
    }

    [Fact]
    public void 接続文字列はファイルと標準入力からも読める()
    {
        var file = Path.Combine(_directory, "target.txt");
        File.WriteAllText(file, "\n  Host=fromfile;Password=FilePass1  \nignored\n");
        var result = Run(
            ["convert", "--from", "sqlserver", "--to", "postgresql", "--source-connection-stdin", "--target-connection-file", file],
            stdin: "\nServer=fromstdin;Password=StdinPass1\n");
        Assert.Equal(0, result.Code);
        Assert.Equal(["Server=fromstdin;Password=StdinPass1", "Host=fromfile;Password=FilePass1"], result.Opener.ConnectionStrings);
    }

    [Fact]
    public void 存在しない接続文字列ファイルは終了コード2()
    {
        var result = Run(["convert", "--from", "sqlserver", "--to", "postgresql", "--target-connection-file", Path.Combine(_directory, "none.txt")], DefaultEnvironment);
        Assert.Equal(2, result.Code);
    }

    [Fact]
    public void 接続文字列とパスワードは出力へ出さない()
    {
        var (source, _) = Databases();
        var opener = new FakeOpener(source, new InMemoryDatabase(Dbms.PostgreSql))
        {
            FailOnOpenSource = new InvalidOperationException("login failed for Server=src;User Id=sa;Password=SourceSecret!; (password SourceSecret!)"),
        };
        var result = Run(["convert", "--from", "sqlserver", "--to", "postgresql"], DefaultEnvironment, opener: opener);

        Assert.Equal(1, result.Code);
        Assert.Contains("Could not open the source database", result.Err);
        var all = result.Out + result.Err;
        Assert.DoesNotContain("SourceSecret", all);
        Assert.DoesNotContain("TargetSecret", all);
        Assert.DoesNotContain("User Id=sa", all);
    }

    [Fact]
    public void ドライランの終了コードと出力()
    {
        var result = Run(["convert", "--from", "sqlserver", "--to", "postgresql", "--dry-run"], DefaultEnvironment);
        Assert.Equal(0, result.Code);
        Assert.Contains("dry-run", result.Out);
        Assert.Contains("nothing was written", result.Out);
    }

    [Fact]
    public void コンソールへ出す文字列はASCIIだけ()
    {
        var result = Run(["convert", "--from", "sqlserver", "--to", "postgresql"], DefaultEnvironment);
        Assert.All(result.Out + result.Err, c => Assert.True(c < 128 || c == '—', $"non-ASCII character: {c}"));
    }
}

[Collection("SecretRedactor")]
public sealed class SecretRedactorTests
{
    [Fact]
    public void 登録した接続文字列とパスワードを伏せる()
    {
        SecretRedactor.Clear();
        SecretRedactor.RegisterConnectionString("Server=a;User Id=sa;Password=Sup3rSecret");
        var text = SecretRedactor.Redact("failed: Server=a;User Id=sa;Password=Sup3rSecret / Sup3rSecret");
        Assert.DoesNotContain("Sup3rSecret", text);
        SecretRedactor.Clear();
    }

    [Fact]
    public void 短すぎる文字列は登録しない()
    {
        SecretRedactor.Clear();
        SecretRedactor.Register("ab");
        Assert.Equal("abc", SecretRedactor.Redact("abc"));
        SecretRedactor.Clear();
    }
}
