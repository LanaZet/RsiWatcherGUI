namespace RsiWatcherCLI.Core;

public interface IAlertSink { Task NotifyAsync(string message, CancellationToken ct); }

public sealed class CompositeAlertSink : IAlertSink
{
    private readonly IReadOnlyList<IAlertSink> _sinks;
    public CompositeAlertSink(params IAlertSink[] sinks) => _sinks = sinks;
    public async Task NotifyAsync(string message, CancellationToken ct) { foreach (var s in _sinks) await s.NotifyAsync(message, ct); }
}

public sealed class ConsoleAlertSink : IAlertSink
{
    public Task NotifyAsync(string message, CancellationToken ct) { Console.WriteLine("ALERT: " + message); return Task.CompletedTask; }
}

public sealed class TelegramAlertSink : IAlertSink
{
    private readonly HttpClient _http; private readonly Func<(string? token, string? chat)> _creds;
    public TelegramAlertSink(HttpClient http, Func<(string? token, string? chat)> creds) { _http = http; _creds = creds; }
    public async Task NotifyAsync(string message, CancellationToken ct)
    {
        var (token, chat) = _creds(); if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chat)) return;
        try { var url = $"https://api.telegram.org/bot{token}/sendMessage"; var form = new Dictionary<string, string> { ["chat_id"] = chat!, ["text"] = message, ["disable_web_page_preview"] = "true" };
            using var resp = await _http.PostAsync(url, new FormUrlEncodedContent(form), ct); }
        catch { }
    }
}

public sealed class ConsoleLogger : ILogger
{
    public void Info(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    public void Error(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ERROR: {message}");
}
