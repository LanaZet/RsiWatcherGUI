namespace RsiWatcherGUI.Core;

public interface IRsiMonitor
{
    Task StartAsync(RsiMonitorSettings settings, CancellationToken ct);
}

public sealed record RsiMonitorSettings(
    string InstrumentRoot,
    string? ManualSecid,
    bool UseAutoFront,
    int PollSeconds,
    Dictionary<Timeframe, TfConfig> Frames
);

public sealed class RsiMonitor : IRsiMonitor
{
    private readonly IMoexClient _moex;
    private readonly IAggregator _agg;
    private readonly IRsiCalculator _rsi;
    private readonly IContractResolver _resolver;
    private readonly IAlertSink _alerts;
    private readonly ITimeProvider _time;
    private readonly ILogger _log;

    public RsiMonitor(IMoexClient moex, IAggregator agg, IRsiCalculator rsi,
                      IContractResolver resolver, IAlertSink alerts, ITimeProvider time, ILogger log)
    {
        _moex = moex; _agg = agg; _rsi = rsi; _resolver = resolver; _alerts = alerts; _time = time; _log = log;
    }

    public async Task StartAsync(RsiMonitorSettings s, CancellationToken ct)
    {
        var secid = s.UseAutoFront
            ? await _resolver.ResolveActiveAsync(s.InstrumentRoot, ct)
            : (s.ManualSecid ?? throw new InvalidOperationException("SECID не указан."));

        _log.Info($"SECID: {secid}");
        var zones = s.Frames.Keys.ToDictionary(tf => tf, _ => Zone.Neutral);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var candles1m = await _moex.GetCandlesAsync(secid, interval: 1, limit: 6000, ct);
                var pieces = new List<string>();

                foreach (var (tf, cfg) in s.Frames.OrderBy(x => x.Key))
                {
                    if (!cfg.Enabled) continue;

                    var frame = TimeSpan.FromMinutes((int)tf);
                    var agg = _agg.Aggregate(candles1m, frame, _time);
                    var closes = agg.Select(x => x.Close).ToList();
                    var rsi = _rsi.ComputeLatest(closes, cfg.Period);

                    pieces.Add($"{(int)tf}:{(rsi?.ToString("F1") ?? "n/a")}");

                    if (rsi is double val)
                    {
                        if (val >= cfg.OB && zones[tf] != Zone.Overbought)
                        {
                            zones[tf] = Zone.Overbought;
                            var msg = $"{secid} TF{(int)tf}m: Перекуплен (RSI={val:F2} ≥ {cfg.OB})";
                            _log.Info(msg);
                            await _alerts.NotifyAsync(msg, ct);
                        }
                        else if (val <= cfg.OS && zones[tf] != Zone.Oversold)
                        {
                            zones[tf] = Zone.Oversold;
                            var msg = $"{secid} TF{(int)tf}m: Перепродан (RSI={val:F2} ≤ {cfg.OS})";
                            _log.Info(msg);
                            await _alerts.NotifyAsync(msg, ct);
                        }
                        else if (val < cfg.OB && val > cfg.OS && zones[tf] != Zone.Neutral)
                        {
                            zones[tf] = Zone.Neutral;
                            _log.Info($"{secid} TF{(int)tf}m: Возврат в диапазон ({cfg.OS}..{cfg.OB})");
                        }
                    }
                }

                _log.Info($"[{secid}] {string.Join("  ", pieces)}  @ {_time.Now:HH:mm:ss}");
            }
            catch (Exception ex)
            {
                _log.Error("Ошибка цикла: " + ex.Message);
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(3, s.PollSeconds)), ct);
        }
    }
}
