namespace RsiWatcherCLI.Core;

public interface IRsiCalculator { double? ComputeLatest(IReadOnlyList<double> closes, int period); }

public sealed class WilderRsiCalculator : IRsiCalculator
{
    public double? ComputeLatest(IReadOnlyList<double> closes, int period)
    {
        if (closes == null || closes.Count < period + 1) return null;
        double gainSum = 0, lossSum = 0;
        for (int i = 1; i <= period; i++) { var ch = closes[i] - closes[i - 1]; if (ch > 0) gainSum += ch; else lossSum += -ch; }
        double avgGain = gainSum / period, avgLoss = lossSum / period;
        for (int i = period + 1; i < closes.Count; i++)
        {
            var ch = closes[i] - closes[i - 1]; var gain = Math.Max(ch, 0); var loss = Math.Max(-ch, 0);
            avgGain = (avgGain * (period - 1) + gain) / period;
            avgLoss = (avgLoss * (period - 1) + loss) / period;
        }
        double rs = avgLoss == 0 ? double.PositiveInfinity : avgGain / avgLoss;
        return 100.0 - (100.0 / (1.0 + rs));
    }
}
