using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace RsiWatcherCLI.Core;

public interface IMoexClient
{
    Task<IReadOnlyList<Candle>> GetCandlesAsync(string secid, int interval, int limit, CancellationToken ct);
    Task<DateTime> GetLastCandleEndAsync(string secid, int interval, CancellationToken ct);
    Task<IReadOnlyList<string>> ListSecuritiesAsync(CancellationToken ct);
}

public sealed class MoexIssClient : IMoexClient
{
    private readonly HttpClient _http;
    private readonly string _engine = "futures", _market = "forts", _board = "RFUD";
    public MoexIssClient(HttpClient http) { _http = http; _http.DefaultRequestHeaders.UserAgent.ParseAdd("rsi-watcher-cli/1.0"); }

    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(string secid, int interval, int limit, CancellationToken ct)
    {
        var url = $"https://iss.moex.com/iss/engines/{_engine}/markets/{_market}/boards/{_board}/securities/{secid}/candles.json?interval={interval}&limit={limit}";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);

        var node = json?["candles"]?.AsObject() ?? throw new InvalidOperationException("No 'candles' node.");
        var cols = node["columns"]!.AsArray().Select(n => n!.ToString()).ToList();
        var data = node["data"]!.AsArray();

        int iBegin = IndexOf(cols, "begin"), iEnd = IndexOf(cols, "end"),
            iOpen = IndexOf(cols, "open"), iHigh = IndexOf(cols, "high"),
            iLow = IndexOf(cols, "low"), iClose = IndexOf(cols, "close"), iVol = IndexOf(cols, "volume");

        var list = new List<Candle>(data.Count);
        foreach (var row in data)
        {
            if (row is not JsonArray a) continue;
            var begin = ParseIso(a[iBegin]?.ToString());
            var end = ParseIso(a[iEnd]?.ToString());
            list.Add(new Candle(begin, end, ParseDouble(a[iOpen]), ParseDouble(a[iHigh]), ParseDouble(a[iLow]), ParseDouble(a[iClose]), ParseLong(a[iVol])));
        }
        list.Sort((x, y) => x.Begin.CompareTo(y.Begin));
        return list;
    }

    public async Task<DateTime> GetLastCandleEndAsync(string secid, int interval, CancellationToken ct)
    {
        var url = $"https://iss.moex.com/iss/engines/{_engine}/markets/{_market}/boards/{_board}/securities/{secid}/candles.json?interval={interval}&limit=1";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
        var node = json?["candles"]?.AsObject() ?? throw new InvalidOperationException("No 'candles' node.");
        var cols = node["columns"]!.AsArray().Select(n => n!.ToString()).ToList();
        var data = node["data"]!.AsArray();
        if (data.Count == 0) return DateTime.MinValue;
        int iEnd = IndexOf(cols, "end");
        var endStr = (data[0] as JsonArray)?[iEnd]?.ToString() ?? "";
        return ParseIso(endStr);
    }

    public async Task<IReadOnlyList<string>> ListSecuritiesAsync(CancellationToken ct)
    {
        var url = $"https://iss.moex.com/iss/engines/{_engine}/markets/{_market}/boards/{_board}/securities.json";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
        var node = json?["securities"]?.AsObject() ?? throw new InvalidOperationException("No 'securities' node.");
        var cols = node["columns"]!.AsArray().Select(n => n!.ToString()).ToList();
        var data = node["data"]!.AsArray();
        int idxSecid = IndexOf(cols, "SECID");
        var list = new List<string>(data.Count);
        foreach (var row in data)
            if (row is JsonArray a && a[idxSecid] != null) list.Add(a[idxSecid]!.ToString());
        return list;
    }

    private static int IndexOf(List<string> cols, string name)
    {
        var i = cols.FindIndex(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0) throw new InvalidOperationException($"Column '{name}' not found.");
        return i;
    }

    private static DateTime ParseIso(string? s) => DateTime.Parse(s ?? "", CultureInfo.InvariantCulture, DateTimeStyles.None);
    private static double ParseDouble(JsonNode? n) => double.Parse(n!.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture);
    private static long ParseLong(JsonNode? n) => long.TryParse(n?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0L;
}
