using System.Globalization;
using RsiWatcherCLI.Core;

Console.WriteLine("RSI Watcher CLI — starting...");

var http = new HttpClient() { Timeout = TimeSpan.FromSeconds(20) };
var moex = new MoexIssClient(http);
var agg = new AggregationService();
var rsi = new WilderRsiCalculator();
var resolver = new FortsFrontResolver(moex);
var time = new SystemTimeProvider();

var sinks = new List<IAlertSink> { new ConsoleAlertSink() };
var alerts = new CompositeAlertSink(sinks.ToArray());

var monitor = new RsiMonitor(moex, agg, rsi, resolver, alerts, time, new ConsoleLogger());

var frames = new Dictionary<Timeframe, TfConfig>
{
    { Timeframe.M5, new TfConfig(14, 70, 30, true) },
    { Timeframe.M15, new TfConfig(14, 70, 30, true) },
    { Timeframe.M30, new TfConfig(14, 70, 30, true) }
};

var settings = new RsiMonitorSettings("Si", null, true, 30, frames);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    await monitor.StartAsync(settings, cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Cancelled.");
}
catch (Exception ex)
{
    Console.WriteLine("Error: " + ex);
}

Console.WriteLine("Exiting...");
