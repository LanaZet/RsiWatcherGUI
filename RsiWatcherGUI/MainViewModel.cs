using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using RsiWatcherGUI.Core;

namespace RsiWatcherGUI
{
    public sealed class MainViewModel : INotifyPropertyChanged, ILogger
    {
        private readonly System.Windows.Forms.NotifyIcon _tray;
        private CancellationTokenSource? _cts;

        // Inputs (bound from UI)
        public string InstrumentRoot { get; set; } = "Si";
        public bool AutoFront { get; set; } = false;
        public string? ManualSecid { get; set; }
        public int Poll { get; set; } = 30;

        public bool Use5m { get; set; } = true;
        public int Rsi5 { get; set; } = 14;
        public double Ob5 { get; set; } = 70;
        public double Os5 { get; set; } = 30;

        public bool Use15m { get; set; } = true;
        public int Rsi15 { get; set; } = 14;
        public double Ob15 { get; set; } = 70;
        public double Os15 { get; set; } = 30;

        public bool Use30m { get; set; } = true;
        public int Rsi30 { get; set; } = 14;
        public double Ob30 { get; set; } = 70;
        public double Os30 { get; set; } = 30;

        public string? TgToken { get; set; }
        public string? TgChat { get; set; }
        public bool Beep { get; set; } = true;
        public bool Balloon { get; set; } = true;

        // Outputs (bound to UI)
        private string _statusText = "Ожидание…";
        public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

        private string _resolvedSecid = "—";
        public string ResolvedSecidText { get => _resolvedSecid; private set => Set(ref _resolvedSecid, value); }

        private string _rsiNow = "—";
        public string RsiNowText { get => _rsiNow; private set => Set(ref _rsiNow, value); }

        public string StartStopText => IsRunning ? "Стоп" : "Старт";

        public bool IsRunning => _cts != null;

        // Logging event for appending lines in the View (TextBox append)
        public event Action<string>? OnLog;

        // INotifyPropertyChanged
        public event PropertyChangedEventHandler? PropertyChanged;

        public MainViewModel(System.Windows.Forms.NotifyIcon tray)
        {
            _tray = tray;
            StartStopCommand = new AsyncRelayCommand(ExecuteStartStopAsync);
            TgTestCommand = new AsyncRelayCommand(ExecuteTgTestAsync);
        }

        // Commands
        public ICommand StartStopCommand { get; }
        public ICommand TgTestCommand { get; }

        private async Task ExecuteTgTestAsync(CancellationToken ct)
        {
            var http = new HttpClient();
            var tg = new TelegramAlertSink(http, () => (TgToken?.Trim(), TgChat?.Trim()));
            await tg.NotifyAsync("Тест от RSI Watcher ✅", ct);
        }

        private async Task ExecuteStartStopAsync(CancellationToken ct)
        {
            // If already running -> stop
            if (_cts != null)
            {
                _cts.Cancel();
                return;
            }

            // validate a bit
            if (Poll < 3) { Info("Опрос должен быть ≥ 3 сек."); return; }

            var frames = new Dictionary<Timeframe, TfConfig>();
            if (Use5m) frames[Timeframe.M5] = new TfConfig(Rsi5, Ob5, Os5, true);
            if (Use15m) frames[Timeframe.M15] = new TfConfig(Rsi15, Ob15, Os15, true);
            if (Use30m) frames[Timeframe.M30] = new TfConfig(Rsi30, Ob30, Os30, true);
            if (frames.Count == 0) { Info("Включите хотя бы один TF."); return; }

            var settings = new RsiMonitorSettings(InstrumentRoot, ManualSecid, AutoFront, Poll, frames);

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(StartStopText));
            StatusText = "Работает…";

            // build services
            var httpMoex = new HttpClient();
            var moex = new MoexIssClient(httpMoex, "futures", "forts", "RFUD");
            var agg = new AggregationService();
            var rsi = new WilderRsiCalculator();
            var resolver = new FortsFrontResolver(moex);
            var time = new SystemTimeProvider();

            var httpTg = new HttpClient();
            var sinks = new List<IAlertSink>();
            if (Beep) sinks.Add(new BeepAlertSink());
            if (Balloon) sinks.Add(new TrayAlertSink(_tray));
            sinks.Add(new TelegramAlertSink(httpTg, () => (TgToken?.Trim(), TgChat?.Trim())));
            var alerts = new CompositeAlertSink(sinks.ToArray());

            var monitor = new RsiMonitor(moex, agg, rsi, resolver, alerts, time, this);

            try
            {
                await monitor.StartAsync(settings, _cts.Token);
            }
            catch (OperationCanceledException) { /* normal */ }
            catch (Exception ex) { Error("Фатальная ошибка: " + ex.Message); }
            finally
            {
                _cts = null;
                StatusText = "Остановлено.";
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(StartStopText));
            }
        }

        // ILogger implementation used by RsiMonitor and friends
        public void Info(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            OnLog?.Invoke(line);

            if (message.StartsWith("SECID:", StringComparison.OrdinalIgnoreCase))
            {
                var sec = message.Length > 6 ? message.Substring(6).Trim() : null;
                if (!string.IsNullOrEmpty(sec)) ResolvedSecidText = sec!;
            }

            var idx = message.IndexOf("] ", StringComparison.Ordinal);
            if (idx >= 0 && idx + 2 < message.Length)
            {
                var tail = message.Substring(idx + 2);
                RsiNowText = tail;
            }
        }

        public void Error(string message) => OnLog?.Invoke($"[{DateTime.Now:HH:mm:ss}] ERROR: {message}");

        // helper: set and notify
        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (!EqualityComparer<T>.Default.Equals(field, value))
            {
                field = value;
                OnPropertyChanged(name);
            }
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // removed unused helper Set<T>(T value...) — use Set(ref field, value) or OnPropertyChanged directly

        // AsyncRelayCommand implementation (small)
        private sealed class AsyncRelayCommand : ICommand
        {
            private readonly Func<CancellationToken, Task> _execute;
            private CancellationTokenSource? _running;

            public AsyncRelayCommand(Func<CancellationToken, Task> execute) => _execute = execute;

            public bool CanExecute(object? parameter) => _running == null;
            public event EventHandler? CanExecuteChanged;

            public async void Execute(object? parameter)
            {
                if (_running != null) { _running.Cancel(); return; }
                _running = new CancellationTokenSource();
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
                try { await _execute(_running.Token); }
                finally { _running = null; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
            }
        }
    }
}
