using Avalonia.Controls;
using Avalonia.Interactivity;
using RsiWatcherCLI.Core;

namespace RsiWatcherAvalonia;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _cts;
    private RsiMonitor? _monitor;

        public MainWindow()
        {
            InitializeComponent();
            var vm = new MainWindowViewModel();
            DataContext = vm;

            // Keep UI logger for older code paths (TextBox named LogBox)
            var logBox = this.FindControl<TextBox>("LogBox");
            if (logBox != null)
            {
                var uiLogger = new UiLogger(logBox);
                var uiAlert = new UiAlertSink(logBox);
                // Subscribe ViewModel logs into the old LogBox so both show messages
                vm.PropertyChanged += (_, __) => { };
            }
        }

    private sealed class UiLogger : RsiWatcherCLI.Core.ILogger
    {
        private readonly TextBox _log;
        public UiLogger(TextBox log) => _log = log;
        public void Info(string message)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _log.Text += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
                _log.CaretIndex = _log.Text?.Length ?? 0;
            });
        }
        public void Error(string message)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _log.Text += $"[{DateTime.Now:HH:mm:ss}] ОШИБКА: {message}\n";
                _log.CaretIndex = _log.Text?.Length ?? 0;
            });
        }
    }

    private sealed class UiAlertSink : RsiWatcherCLI.Core.IAlertSink
    {
        private readonly TextBox _log;
        public UiAlertSink(TextBox log) => _log = log;
        public Task NotifyAsync(string message, CancellationToken ct)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _log.Text += $"ОПОВЕЩЕНИЕ: {message}\n";
                _log.CaretIndex = _log.Text?.Length ?? 0;
            });
            return Task.CompletedTask;
        }
    }
}
