namespace RsiWatcherCLI.Core;

public interface IAggregator { IReadOnlyList<Candle> Aggregate(IReadOnlyList<Candle> src, TimeSpan frame, ITimeProvider time); }

public sealed class AggregationService : IAggregator
{
    public IReadOnlyList<Candle> Aggregate(IReadOnlyList<Candle> src, TimeSpan frame, ITimeProvider time)
    {
        var buckets = new SortedDictionary<DateTime, List<Candle>>();
        foreach (var c in src)
        {
            var b = FloorToFrame(c.Begin, frame);
            if (!buckets.TryGetValue(b, out var list)) { list = new(); buckets[b] = list; }
            list.Add(c);
        }

        var result = new List<Candle>(buckets.Count);
        foreach (var (begin, list) in buckets)
        {
            if (list.Count == 0) continue;
            list.Sort((a, b) => a.Begin.CompareTo(b.Begin));
            var end = begin + frame;
            if (!IsFullFrame(end, time.Now)) continue;
            result.Add(new Candle(begin, end, list.First().Open, list.Max(x => x.High), list.Min(x => x.Low), list.Last().Close, list.Sum(x => x.Volume)));
        }
        return result;
    }

    private static DateTime FloorToFrame(DateTime t, TimeSpan frame) => new DateTime((t.Ticks / frame.Ticks) * frame.Ticks, t.Kind);
    private static bool IsFullFrame(DateTime frameEnd, DateTime now) => frameEnd <= now;
}
