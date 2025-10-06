using System.Text.RegularExpressions;

namespace RsiWatcherCLI.Core;

public interface IContractResolver { Task<string> ResolveActiveAsync(string root, CancellationToken ct); }

public sealed class FortsFrontResolver : IContractResolver
{
    private readonly IMoexClient _client;
    public FortsFrontResolver(IMoexClient client) => _client = client;

    public async Task<string> ResolveActiveAsync(string root, CancellationToken ct)
    {
        var all = await _client.ListSecuritiesAsync(ct);
        var re = new Regex("^" + Regex.Escape(root) + "[FGHJKMNQUVXZ][0-9]$", RegexOptions.IgnoreCase);
        var candidates = all.Where(id => re.IsMatch(id)).ToList();
        if (candidates.Count == 0) throw new InvalidOperationException($"No contracts for {root}.");
        DateTime best = DateTime.MinValue; string? bestSec = null;
        foreach (var sec in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try { var last = await _client.GetLastCandleEndAsync(sec, interval: 60, ct); if (last > best) { best = last; bestSec = sec; } }
            catch { }
        }
        return bestSec ?? throw new InvalidOperationException($"Cannot resolve front for {root}.");
    }
}
