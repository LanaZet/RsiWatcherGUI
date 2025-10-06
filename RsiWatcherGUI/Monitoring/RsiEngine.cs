using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RsiWatcherGUI.Core
{
    public sealed class RsiEngine
    {
        private readonly HttpClient _http;
        public const string Engine = "futures";
        public const string Market = "forts";
        public const string Board = "RFUD";
        public const int Interval1m = 1;

        public RsiEngine(HttpClient http)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        public async Task<string> ResolveActiveContractAsync(string root)
        {
            var all = await ListSecuritiesAsync();
            var re = new Regex("^" + Regex.Escape(root) + "[FGHJKMNQUVXZ][0-9]$", RegexOptions.IgnoreCase);

            var candidates = all.Where(id => re.IsMatch(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (candidates.Count == 0)
                throw new Exception($"Не нашёл активные контракты для корня {root}.");

            DateTime best = DateTime.MinValue;
            string? bestSec = null;
            foreach (var sec in candidates)
            {
                try
                {
                    var last = await FetchLastCandleEndAsync(sec, interval: 60);
                    if (last > best) { best = last; bestSec = sec; }
                }
                catch { }
            }
            if (bestSec == null) throw new Exception($"Не удалось определить активный контракт для {root}.");
            return bestSec;
        }

        public async Task<List<string>> ListSecuritiesAsync()
        {
            var url = $"https://iss.moex.com/iss/engines/{Engine}/markets/{Market}/boards/{Board}/securities.json";
            using var resp = await _http.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadFromJsonAsync<JsonObject>();

            var node = json?["securities"]?.AsObject() ?? throw new Exception("Нет секции securities.");
            var cols = node["columns"]!.AsArray().Select(n => n!.ToString()).ToList();
            var data = node["data"]!.AsArray();

            int idxSecid = cols.FindIndex(c => c.Equals("SECID", StringComparison.OrdinalIgnoreCase));
            if (idxSecid < 0) throw new Exception("Поле SECID не найдено.");

            var list = new List<string>(data.Count);
            foreach (var row in data)
            {
                if (row is JsonArray arr && arr[idxSecid] != null)
                    list.Add(arr[idxSecid]!.ToString());
            }
            return list;
        }

        public async Task<DateTime> FetchLastCandleEndAsync(string secid, int interval)
        {
            var url = $"https://iss.moex.com/iss/engines/{Engine}/markets/{Market}/boards/{Board}/securities/{secid}/candles.json?interval={interval}&limit=1";
            using var resp = await _http.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadFromJsonAsync<JsonObject>();

            var node = json?["candles"]?.AsObject() ?? throw new Exception("Нет секции candles");
            var cols = node["columns"]!.AsArray().Select(n => n!.ToString()).ToList();
            var data = node["data"]!.AsArray();
            if (data.Count == 0) return DateTime.MinValue;

            int idxEnd = cols.FindIndex(c => c.Equals("end", StringComparison.OrdinalIgnoreCase));
            var endStr = (data[0] as JsonArray)?[idxEnd]?.ToString() ?? "";
            return ParseIso(endStr);
        }

        public async Task<List<Candle>> FetchCandles1mAsync(string secid, int limit)
        {
            var url = $"https://iss.moex.com/iss/engines/{Engine}/markets/{Market}/boards/{Board}/securities/{secid}/candles.json?interval={Interval1m}&limit={limit}";
            using var resp = await _http.GetAsync(url);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadFromJsonAsync<JsonObject>();
            var node = json?["candles"]?.AsObject() ?? throw new Exception("Нет секции candles.");

            var cols = node["columns"]!.AsArray().Select(n => n!.ToString()).ToList();
            var data = node["data"]!.AsArray();

            int idxBegin = cols.IndexOf("begin");
            int idxEnd = cols.IndexOf("end");
            int idxOpen = cols.IndexOf("open");
            int idxHigh = cols.IndexOf("high");
            int idxLow = cols.IndexOf("low");
            int idxClose = cols.IndexOf("close");
            int idxVol = cols.IndexOf("volume");

            var list = new List<Candle>(data.Count);
            foreach (var row in data)
            {
                if (row is not JsonArray arr) continue;
                var c = new Candle(
                    Begin: ParseIso(arr[idxBegin]?.ToString()),
                    End: ParseIso(arr[idxEnd]?.ToString()),
                    Open: ParseDouble(arr[idxOpen]),
                    High: ParseDouble(arr[idxHigh]),
                    Low: ParseDouble(arr[idxLow]),
                    Close: ParseDouble(arr[idxClose]),
                    Volume: ParseLong(arr[idxVol])
                );
                list.Add(c);
            }
            list.Sort((a, b) => a.Begin.CompareTo(b.Begin));
            return list;
        }

        public static List<Candle> Aggregate(List<Candle> src, TimeSpan tf)
        {
            var buckets = new SortedDictionary<DateTime, List<Candle>>();
            foreach (var c in src)
            {
                var b = FloorToFrame(c.Begin, tf);
                if (!buckets.TryGetValue(b, out var list)) { list = new(); buckets[b] = list; }
                list.Add(c);
            }

            var result = new List<Candle>(buckets.Count);
            foreach (var kv in buckets)
            {
                var list = kv.Value;
                if (list.Count == 0) continue;
                list.Sort((a, b) => a.Begin.CompareTo(b.Begin));

                var begin = kv.Key;
                var end = begin + tf;

                // исключаем незавершённые
                if (end > DateTime.Now) continue;

                result.Add(new Candle(
                    begin,
                    end,
                    Open: list.First().Open,
                    High: list.Max(x => x.High),
                    Low: list.Min(x => x.Low),
                    Close: list.Last().Close,
                    Volume: list.Sum(x => x.Volume)
                ));
            }
            return result;
        }

        public static double? ComputeRsi(List<double> closes, int period)
        {
            if (closes == null || closes.Count < period + 1) return null;

            double gainSum = 0, lossSum = 0;
            for (int i = 1; i <= period; i++)
            {
                var ch = closes[i] - closes[i - 1];
                if (ch > 0) gainSum += ch; else lossSum += -ch;
            }
            double avgGain = gainSum / period;
            double avgLoss = lossSum / period;

            for (int i = period + 1; i < closes.Count; i++)
            {
                var ch = closes[i] - closes[i - 1];
                var gain = Math.Max(ch, 0);
                var loss = Math.Max(-ch, 0);
                avgGain = (avgGain * (period - 1) + gain) / period;
                avgLoss = (avgLoss * (period - 1) + loss) / period;
            }

            double rs = avgLoss == 0 ? double.PositiveInfinity : avgGain / avgLoss;
            return 100.0 - (100.0 / (1.0 + rs));
        }

        static DateTime FloorToFrame(DateTime t, TimeSpan frame)
        {
            var ticks = frame.Ticks;
            return new DateTime((t.Ticks / ticks) * ticks, t.Kind);
        }

        static string Fmt(double? v) => v is null ? "n/a" : v.Value.ToString("F2");

        static DateTime ParseIso(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return DateTime.MinValue;
            return DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
        }
        static double ParseDouble(JsonNode? n)
            => double.Parse(n!.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture);
        static long ParseLong(JsonNode? n)
            => long.TryParse(n?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0L;

        public record Candle(DateTime Begin, DateTime End, double Open, double High, double Low, double Close, long Volume);
    }
}
