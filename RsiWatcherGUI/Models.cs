namespace RsiWatcherGUI.Core;

public record Candle(DateTime Begin, DateTime End, double Open, double High, double Low, double Close, long Volume);

public enum Zone { Neutral, Overbought, Oversold }

public sealed record TfConfig(int Period, double OB, double OS, bool Enabled);

public enum Timeframe
{
    M5  = 5,
    M15 = 15,
    M30 = 30
}

public interface ILogger
{
    void Info(string message);
    void Error(string message);
}

public interface ITimeProvider
{
    DateTime Now { get; }
}

public sealed class SystemTimeProvider : ITimeProvider
{
    public DateTime Now => DateTime.Now;
}
