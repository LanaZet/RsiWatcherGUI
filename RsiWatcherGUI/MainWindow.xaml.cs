using System.Windows;
using System.Windows.Controls;
using RsiWatcherGUI.Core;
using System.Net.Http;
using MessageBox = System.Windows.Forms.MessageBox;
using TextBox = System.Windows.Controls.TextBox;

namespace RsiWatcherGUI
{
    public partial class MainWindow : Window, ILogger
    {
        private CancellationTokenSource? _cts;
        private NotifyIcon _tray;
        private IAutostartService _autostart;

        public MainWindow()
        {
            InitializeComponent();

            // Трей-иконка (переиспользуем в алертах)
            _tray = new NotifyIcon
            {
                Visible = true,
                Icon = SystemIcons.Information,
                Text = "RSI Watcher (SOLID)"
            };

            _autostart = new RegistryAutostartService("RsiWatcherGUI");
            AutostartCheck.IsChecked = _autostart.IsEnabled();

            AutoFrontCheck.Checked += (_, __) => ManualSecidBox.IsEnabled = false;
            AutoFrontCheck.Unchecked += (_, __) => ManualSecidBox.IsEnabled = true;
        }

        // ===== UI events =====
        private void AutostartCheck_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (AutostartCheck.IsChecked == true) _autostart.Enable();
                else _autostart.Disable();
            }
            catch (Exception ex) { MessageBox.Show("Автозапуск: " + ex.Message); }
        }

        private async void StartStopBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts = null;
                StartStopBtn.Content = "Старт";
                StatusText.Text = "Остановлено.";
                return;
            }

            if (!int.TryParse(PollBox.Text, out var poll) || poll < 3) { MessageBox.Show("Опрос ≥ 3 сек."); return; }

            var frames = new Dictionary<Timeframe, TfConfig>();
            if (Use5m.IsChecked == true && TryReadTf(Rsi5Box, Ob5Box, Os5Box, out var c5)) frames[Timeframe.M5] = c5!;
            if (Use15m.IsChecked == true && TryReadTf(Rsi15Box, Ob15Box, Os15Box, out var c15)) frames[Timeframe.M15] = c15!;
            if (Use30m.IsChecked == true && TryReadTf(Rsi30Box, Ob30Box, Os30Box, out var c30)) frames[Timeframe.M30] = c30!;
            if (frames.Count == 0) { MessageBox.Show("Включите хотя бы один TF."); return; }

            var instrumentRoot = GetSelectedRoot();
            var manualSecid = ManualSecidBox.Text?.Trim();
            bool autoFront = AutoFrontCheck.IsChecked == true;

            // --- Сборка сервисов (простая DI) ---
            var httpMoex = new HttpClient();
            var moex = new MoexIssClient(httpMoex, "futures", "forts", "RFUD");
            var agg = new AggregationService();
            var rsi = new WilderRsiCalculator();
            var resolver = new FortsFrontResolver(moex);
            var time = new SystemTimeProvider();

            var httpTg = new HttpClient();
            var alerts = new List<IAlertSink>();
            if (BeepCheck.IsChecked == true) alerts.Add(new BeepAlertSink());
            if (BalloonCheck.IsChecked == true) alerts.Add(new TrayAlertSink(_tray));
            alerts.Add(new TelegramAlertSink(httpTg, () => (TgTokenBox.Text?.Trim(), TgChatBox.Text?.Trim())));
            var alertComposite = new CompositeAlertSink(alerts.ToArray());

            var monitor = new RsiMonitor(moex, agg, rsi, resolver, alertComposite, time, this);

            _cts = new CancellationTokenSource();
            var settings = new RsiMonitorSettings(instrumentRoot, manualSecid, autoFront, poll, frames);

            StartStopBtn.Content = "Стоп";
            StatusText.Text = "Работает…";

            try
            {
                await monitor.StartAsync(settings, _cts.Token); // завершится по Cancel
            }
            catch (OperationCanceledException) { /* ok */ }
            catch (Exception ex)
            {
                Error("Фатальная ошибка: " + ex.Message);
                MessageBox.Show(ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                StartStopBtn.Content = "Старт";
                StatusText.Text = "Остановлено.";
                _cts = null;
            }
        }

        private void TgTestBtn_Click(object sender, RoutedEventArgs e)
        {
            _ = new TelegramAlertSink(new HttpClient(), () => (TgTokenBox.Text?.Trim(), TgChatBox.Text?.Trim()))
                .NotifyAsync("Тест от RSI Watcher ✅", CancellationToken.None);
        }

        // ===== Helpers =====
        private static bool TryReadTf(TextBox rsiBox, TextBox obBox, TextBox osBox, out TfConfig? cfg)
        {
            cfg = null;
            if (!int.TryParse(rsiBox.Text, out var p) || p < 2) return false;
            if (!double.TryParse(obBox.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var ob)) return false;
            if (!double.TryParse(osBox.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var os)) return false;
            cfg = new TfConfig(p, ob, os, true);
            return true;
        }

        private string GetSelectedRoot()
        {
            if (InstrumentCombo.SelectedItem is ComboBoxItem it && it.Tag is string s) return s;
            return "Si";
        }

        // ===== ILogger =====
        public void Info(string message)
        {
            Dispatcher.Invoke(() =>
            {
                LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
                LogBox.ScrollToEnd();
                RsiNowText.Text = message.Contains("] ") ? message.Split("] ").Last() : RsiNowText.Text;
            });
        }

        public void Error(string message)
        {
            Dispatcher.Invoke(() =>
            {
                LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] ERROR: {message}\n");
                LogBox.ScrollToEnd();
            });
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {

        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {

        }
    }
}
