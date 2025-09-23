using System.Media;
using Forms = System.Windows.Forms;

namespace RsiWatcherGUI.Core;

public interface IAlertSink
{
    Task NotifyAsync(string message, CancellationToken ct);
}

public sealed class CompositeAlertSink : IAlertSink
{
    private readonly IReadOnlyList<IAlertSink> _sinks;
    public CompositeAlertSink(params IAlertSink[] sinks) => _sinks = sinks;
    public async Task NotifyAsync(string message, CancellationToken ct)
    {
        foreach (var s in _sinks)
            await s.NotifyAsync(message, ct);
    }
}

public sealed class BeepAlertSink : IAlertSink
{
    public Task NotifyAsync(string message, CancellationToken ct)
    {
        try { SystemSounds.Exclamation.Play(); } catch { }
        return Task.CompletedTask;
    }
}

public sealed class TrayAlertSink : IAlertSink
{
    private readonly Forms.NotifyIcon _icon;
    public TrayAlertSink(Forms.NotifyIcon icon) => _icon = icon;

    public Task NotifyAsync(string message, CancellationToken ct)
    {
        try
        {
            _icon.BalloonTipTitle = "RSI сигнал";
            _icon.BalloonTipText = message;
            _icon.ShowBalloonTip(3000);
        }
        catch { }
        return Task.CompletedTask;
    }
}

public sealed class TelegramAlertSink : IAlertSink
{
    private readonly HttpClient _http;
    private readonly Func<(string? token, string? chat)> _creds;

    public TelegramAlertSink(HttpClient http, Func<(string? token, string? chat)> creds)
    {
        _http = http;
        _creds = creds;
    }

    public async Task NotifyAsync(string message, CancellationToken ct)
    {
        var (token, chat) = _creds();
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chat)) return;

        try
        {
            var url = $"https://api.telegram.org/bot{token}/sendMessage";
            var form = new Dictionary<string, string>
            {
                ["chat_id"] = chat!,
                ["text"] = message,
                ["disable_web_page_preview"] = "true"
            };
            using var resp = await _http.PostAsync(url, new FormUrlEncodedContent(form), ct);
            // игнорируем  мониторинг не должен падать из-за телеги
        }
        catch { }
    }
}
