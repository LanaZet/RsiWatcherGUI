using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using Avalonia.Media;
using System.Text.RegularExpressions;
using RsiWatcherCLI.Core;

namespace RsiWatcherAvalonia
{
    public class MainWindowViewModel : INotifyPropertyChanged, ILogger
    {
        private CancellationTokenSource? _cts;

        // Inputs
    public string InstrumentRoot { get; set; } = "Si";
    private bool _autoFront = true;
    public bool AutoFront { get => _autoFront; set => Set(ref _autoFront, value); }
    public string? ManualSecid { get; set; }
        public int Poll { get; set; } = 30;

        public bool Use5m { get; set; } = true; public int Rsi5 { get; set; } = 14; public double Ob5 { get; set; } = 70; public double Os5 { get; set; } = 30;
        public bool Use15m { get; set; } = true; public int Rsi15 { get; set; } = 14; public double Ob15 { get; set; } = 70; public double Os15 { get; set; } = 30;
        public bool Use30m { get; set; } = true; public int Rsi30 { get; set; } = 14; public double Ob30 { get; set; } = 70; public double Os30 { get; set; } = 30;

        public string? TgToken { get; set; }
        public string? TgChat { get; set; }
        public bool Beep { get; set; } = false;
        public bool Balloon { get; set; } = false;

        // Outputs
        private string _statusText = "Ожидание…"; public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
        private string _resolvedSecid = "—"; public string ResolvedSecidText { get => _resolvedSecid; set => Set(ref _resolvedSecid, value); }
    private string _rsiNow = "—"; public string RsiNowText { get => _rsiNow; set => Set(ref _rsiNow, value); }
    private IBrush _rsiNowBrush = Brushes.Green; public IBrush RsiNowBrush { get => _rsiNowBrush; set => Set(ref _rsiNowBrush, value); }

    // Per-timeframe RSI display and brushes for clearer UI
    private string _rsi5Text = "—"; public string Rsi5Text { get => _rsi5Text; set => Set(ref _rsi5Text, value); }
    private IBrush _rsi5Brush = Brushes.Green; public IBrush Rsi5Brush { get => _rsi5Brush; set => Set(ref _rsi5Brush, value); }

    private string _rsi15Text = "—"; public string Rsi15Text { get => _rsi15Text; set => Set(ref _rsi15Text, value); }
    private IBrush _rsi15Brush = Brushes.Green; public IBrush Rsi15Brush { get => _rsi15Brush; set => Set(ref _rsi15Brush, value); }

    private string _rsi30Text = "—"; public string Rsi30Text { get => _rsi30Text; set => Set(ref _rsi30Text, value); }
    private IBrush _rsi30Brush = Brushes.Green; public IBrush Rsi30Brush { get => _rsi30Brush; set => Set(ref _rsi30Brush, value); }

        public ObservableCollection<string> Logs { get; } = new();

        public ICommand StartStopCommand { get; }
        public ICommand TgTestCommand { get; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public MainWindowViewModel()
        {
            StartStopCommand = new AsyncCommand(StartStopAsync);
            TgTestCommand = new AsyncCommand(TgTestAsync);
        }

        private async Task TgTestAsync(CancellationToken ct)
        {
            var http = new HttpClient();
            var tg = new TelegramAlertSink(http, () => (TgToken?.Trim(), TgChat?.Trim()));
            await tg.NotifyAsync("Тест от RSI Watcher (Avalonia)", ct);
        }

        private async Task StartStopAsync(CancellationToken ct)
        {
            if (_cts != null)
            {
                _cts.Cancel();
                return;
            }

            if (Poll < 3) { Info("Опрос должен быть ≥ 3 сек."); return; }

            var frames = new System.Collections.Generic.Dictionary<Timeframe, TfConfig>();
            if (Use5m) frames[Timeframe.M5] = new TfConfig(Rsi5, Ob5, Os5, true);
            if (Use15m) frames[Timeframe.M15] = new TfConfig(Rsi15, Ob15, Os15, true);
            if (Use30m) frames[Timeframe.M30] = new TfConfig(Rsi30, Ob30, Os30, true);
            if (frames.Count == 0) { Info("Включите хотя бы один TF."); return; }

            var settings = new RsiMonitorSettings(InstrumentRoot, ManualSecid, AutoFront, Poll, frames);

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(StartStopText));
            StatusText = "Работает…";

            var http = new HttpClient();
            var moex = new MoexIssClient(http);
            var agg = new AggregationService();
            var rsi = new WilderRsiCalculator();
            var resolver = new FortsFrontResolver(moex);
            var time = new SystemTimeProvider();

            var sinks = new System.Collections.Generic.List<IAlertSink>();
            if (Beep) sinks.Add(new ConsoleAlertSink());
            if (Balloon) sinks.Add(new ConsoleAlertSink());
            sinks.Add(new TelegramAlertSink(new HttpClient(), () => (TgToken?.Trim(), TgChat?.Trim())));
            var alerts = new CompositeAlertSink(sinks.ToArray());

            var monitor = new RsiMonitor(moex, agg, rsi, resolver, alerts, time, this);

            try
            {
                await monitor.StartAsync(settings, _cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Error("Фатальная ошибка: " + ex.Message); }
            finally
            {
                _cts = null;
                StatusText = "Остановлено.";
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(StartStopText));
            }
        }

        public bool IsRunning => _cts != null;
    public string StartStopText => IsRunning ? "Остановить" : "Запустить";

        // ILogger
        public void Info(string message)
        {
            Dispatcher.UIThread.Post(() => Logs.Add($"[{DateTime.Now:HH:mm:ss}] {message}"));
            if (message.StartsWith("SECID:", StringComparison.OrdinalIgnoreCase))
                Dispatcher.UIThread.Post(() => ResolvedSecidText = message.Substring(6).Trim());
            var idx = message.IndexOf("] ", StringComparison.Ordinal);
            if (idx >= 0)
            {
                var tail = message.Substring(idx + 2);
                Dispatcher.UIThread.Post(() => RsiNowText = tail);

                // Periodic summary format produced by RsiMonitor:
                // "[SECID] 5:72.3  15:68.4  30:n/a  @ HH:mm:ss"
                // Extract all occurrences like "<tf>:<val>" and update the UI.
                var matches = Regex.Matches(tail, "(?<tf>\\d+):(?<val>[0-9]+(?:\\.[0-9]+)?|n/a)");
                if (matches.Count > 0)
                {
                    foreach (Match mm in matches)
                    {
                        var tf = mm.Groups["tf"].Value;
                        var val = mm.Groups["val"].Value;
                        if (tf == "5")
                        {
                            Dispatcher.UIThread.Post(() => Rsi5Text = val == "n/a" ? "—" : val);
                            Dispatcher.UIThread.Post(() => Rsi5Brush = val == "n/a" ? Brushes.Gray : Brushes.Green);
                        }
                        else if (tf == "15")
                        {
                            Dispatcher.UIThread.Post(() => Rsi15Text = val == "n/a" ? "—" : val);
                            Dispatcher.UIThread.Post(() => Rsi15Brush = val == "n/a" ? Brushes.Gray : Brushes.Green);
                        }
                        else if (tf == "30")
                        {
                            Dispatcher.UIThread.Post(() => Rsi30Text = val == "n/a" ? "—" : val);
                            Dispatcher.UIThread.Post(() => Rsi30Brush = val == "n/a" ? Brushes.Gray : Brushes.Green);
                        }
                    }
                }

                // Update overall brush and also process OB/OS textual messages (alerts)
                if (tail.Contains("Перекуплен") || tail.Contains("Перепродан"))
                    Dispatcher.UIThread.Post(() => RsiNowBrush = Brushes.Red);
                else if (tail.Contains("Возврат в диапазон"))
                    Dispatcher.UIThread.Post(() => RsiNowBrush = Brushes.Green);

                // Also try to parse verbose alert messages containing "RSI=..." and update per-TF brushes/values
                var m = Regex.Match(tail, "TF(?<tf>\\d+)m:.*RSI=(?<val>[0-9]+\\.?[0-9]*)");
                if (m.Success)
                {
                    var tf = m.Groups["tf"].Value;
                    var val = m.Groups["val"].Value;
                    if (tf == "5")
                    {
                        Dispatcher.UIThread.Post(() => Rsi5Text = val);
                        if (tail.Contains("Перекуплен") || tail.Contains("Перепродан")) Dispatcher.UIThread.Post(() => Rsi5Brush = Brushes.Red);
                        else if (tail.Contains("Возврат в диапазон")) Dispatcher.UIThread.Post(() => Rsi5Brush = Brushes.Green);
                    }
                    else if (tf == "15")
                    {
                        Dispatcher.UIThread.Post(() => Rsi15Text = val);
                        if (tail.Contains("Перекуплен") || tail.Contains("Перепродан")) Dispatcher.UIThread.Post(() => Rsi15Brush = Brushes.Red);
                        else if (tail.Contains("Возврат в диапазон")) Dispatcher.UIThread.Post(() => Rsi15Brush = Brushes.Green);
                    }
                    else if (tf == "30")
                    {
                        Dispatcher.UIThread.Post(() => Rsi30Text = val);
                        if (tail.Contains("Перекуплен") || tail.Contains("Перепродан")) Dispatcher.UIThread.Post(() => Rsi30Brush = Brushes.Red);
                        else if (tail.Contains("Возврат в диапазон")) Dispatcher.UIThread.Post(() => Rsi30Brush = Brushes.Green);
                    }
                }
            }
            Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(IsRunning)));
            Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(StartStopText)));
        }

    public void Error(string message) => Dispatcher.UIThread.Post(() => Logs.Add($"[{DateTime.Now:HH:mm:ss}] ОШИБКА: {message}"));

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (!System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value))
            {
                field = value; OnPropertyChanged(name);
            }
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private sealed class AsyncCommand : ICommand
        {
            private readonly Func<CancellationToken, Task> _execute; private CancellationTokenSource? _running;
            public AsyncCommand(Func<CancellationToken, Task> execute) => _execute = execute;
            // Always allow Execute so the button can be used to cancel (stop) while the command is running.
            public bool CanExecute(object? parameter) => true;
            public event EventHandler? CanExecuteChanged;
            public async void Execute(object? parameter)
            {
                if (_running != null) { _running.Cancel(); return; }
                _running = new CancellationTokenSource();
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
                try { await _execute(_running.Token); } finally { _running = null; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
            }
        }
    }
}
