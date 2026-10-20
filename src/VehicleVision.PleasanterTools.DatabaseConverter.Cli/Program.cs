using System.Reflection;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.CommandLine;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Databases;

// コンソールへ出す文字列は英語（Azure の Kudu の Debug console で日本語が化けるため）。
// 実体は Core の ConverterApplication。ここは、実際の入出力・環境変数・DB 接続・Ctrl+C を結びつけるだけ。
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // 1 回目は、実行中のバッチを終えてから止める（途中の表は完了の記録が無いので、--resume で入れ直せる）
    if (!cancellation.IsCancellationRequested)
    {
        e.Cancel = true;
        cancellation.Cancel();
        Console.Error.WriteLine("Interrupt received; stopping. Press Ctrl+C again to force quit.");
    }
};

var version = typeof(ConverterApplication).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";
var app = new ConverterApplication(
    new AdoDatabaseOpener(),
    Console.In,
    Console.Out,
    Console.Error,
    Environment.GetEnvironmentVariable,
    version);
return await app.RunAsync(args, cancellation.Token);
