using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;

using RsiWatcherGUI.Core;

namespace RsiWatcherGUI
{
    public class MainForm : Form
    {
        readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
        readonly System.Windows.Forms.Timer _timer = new() { Interval = 30000 };

        // UI
        ComboBox instrumentCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
        CheckBox autoFrontCheck = new() { Text = "Авто-контракт (ближайший)", Checked = true, AutoSize = true };
        TextBox manualSecidBox = new() { Width = 80, Enabled = false, PlaceholderText = "напр. SiZ5" };
        NumericUpDown periodUp = new() { Minimum = 2, Maximum = 200, Value = 14, Width = 60 };
        NumericUpDown obUp = new() { Minimum = 50, Maximum = 100, Value = 70, Width = 60, DecimalPlaces = 0 };
        NumericUpDown osUp = new() { Minimum = 0, Maximum = 50, Value = 30, Width = 60, DecimalPlaces = 0 };
        NumericUpDown pollUp = new() { Minimum = 3, Maximum = 600, Value = 30, Width = 80 };
        CheckBox beepCheck = new() { Text = "Звук", Checked = true, AutoSize = true };
        CheckBox balloonCheck = new() { Text = "Уведомления в трее", Checked = true, AutoSize = true };
        Button startStopBtn = new() { Text = "Старт", Width = 120, Height = 32 };
        Label statusLbl = new() { AutoSize = true, Text = "Ожидание…" };
        Label secidLbl = new() { AutoSize = true, Text = "SECID: —" };
    Label rsiLbl = new() { AutoSize = true, Text = "RSI 5/15/30: —", ForeColor = Color.Green };
        TextBox logBox = new() { Multiline = true, ScrollBars = ScrollBars.Both, ReadOnly = true, WordWrap = true, Dock = DockStyle.Fill };
        Panel statusIndicator = new() { Width = 16, Height = 16, BackColor = Color.Green, Margin = new Padding(6, 6, 0, 0) };
        readonly System.Windows.Forms.Timer _blinkTimer = new() { Interval = 500 };

    NotifyIcon tray = new() { Visible = true, Icon = SystemIcons.Information, Text = "RSI Alerts (MOEX)" };
    private RsiWatcherGUI.Core.IAlertSink? _alerts;
    private System.Threading.CancellationTokenSource? _alertCts;

    bool running = false;
    string? resolvedSecid;
    readonly Dictionary<int, Zone> zones = new() { { 5, Zone.Neutral }, { 15, Zone.Neutral }, { 30, Zone.Neutral } };
    private System.Threading.CancellationTokenSource? _cts;
    private IRsiMonitor? _monitor;

        public MainForm()
        {
            Text = "RSI Watcher (MOEX Futures) — WinForms";
            MinimumSize = new Size(800, 540);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("rsi-watcher-winforms/1.0");
            FormClosing += (_, __) => { try { tray.Dispose(); } catch { } };
            _timer.Tick += (_, __) => _ = TickOnceAsync();

            // Верхняя панель
            var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 120, ColumnCount = 1, RowCount = 3 };
            top.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            top.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            top.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(top);

            var row1 = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
            row1.Controls.Add(new Label { Text = "Инструмент:", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft });
            instrumentCombo.Items.Add(new ComboItem("Доллар–рубль (Si)", "Si"));
            instrumentCombo.Items.Add(new ComboItem("Нефть Brent (BR)", "BR"));
            instrumentCombo.Items.Add(new ComboItem("Индекс РТС (RI)", "RI"));
            instrumentCombo.Items.Add(new ComboItem("Сбербанк (SBRF)", "SBRF"));
            instrumentCombo.Items.Add(new ComboItem("Газпром (GAZR)", "GAZR"));
            instrumentCombo.SelectedIndex = 0;
            row1.Controls.Add(instrumentCombo);

            row1.Controls.Add(autoFrontCheck);
            row1.Controls.Add(new Label { Text = "SECID:", AutoSize = true, Padding = new Padding(16, 8, 0, 0) });
            row1.Controls.Add(manualSecidBox);
            autoFrontCheck.CheckedChanged += (_, __) => manualSecidBox.Enabled = !autoFrontCheck.Checked;

            row1.Controls.Add(new Label { Text = "RSI период:", AutoSize = true, Padding = new Padding(16, 8, 0, 0) });
            row1.Controls.Add(periodUp);

            row1.Controls.Add(startStopBtn);
            startStopBtn.Click += StartStopBtn_Click;

            var row2 = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
            row2.Controls.Add(new Label { Text = "Порог OB (перекуплен):", AutoSize = true });
            row2.Controls.Add(obUp);
            row2.Controls.Add(new Label { Text = "Порог OS (перепродан):", AutoSize = true, Padding = new Padding(16, 0, 0, 0) });
            row2.Controls.Add(osUp);
            row2.Controls.Add(new Label { Text = "Период опроса (сек):", AutoSize = true, Padding = new Padding(16, 0, 0, 0) });
            row2.Controls.Add(pollUp);
            row2.Controls.Add(beepCheck);
            row2.Controls.Add(balloonCheck);

            var row3 = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 6, 0, 6) };
                row3.Controls.Add(new Label { Text = "Статус:", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
                row3.Controls.Add(statusLbl);
                row3.Controls.Add(new Label { Text = " ", AutoSize = true });
                row3.Controls.Add(statusIndicator);
            row3.Controls.Add(new Label { Text = "   ", AutoSize = true });
            row3.Controls.Add(secidLbl);
            row3.Controls.Add(new Label { Text = "   ", AutoSize = true });
            row3.Controls.Add(rsiLbl);

            top.Controls.Add(row1);
            top.Controls.Add(row2);
            top.Controls.Add(row3);

            // Лог
            Controls.Add(logBox);

            // Prepare monitor dependencies
            var moex = new MoexIssClient(_http);
            var agg = new AggregationService();
            var rsi = new WilderRsiCalculator();
            var resolver = new FortsFrontResolver(moex);
            var time = new SystemTimeProvider();
            _monitor = new RsiMonitor(moex, agg, rsi, resolver, _alerts ?? new CompositeAlertSink(), time, new UiLogger(this));

            _blinkTimer.Tick += (s, e) => ToggleIndicator();

            // Compose alerts using available sinks
            var sinks = new List<RsiWatcherGUI.Core.IAlertSink>();
            if (beepCheck.Checked) sinks.Add(new RsiWatcherGUI.Core.BeepAlertSink());
            if (balloonCheck.Checked) sinks.Add(new RsiWatcherGUI.Core.TrayAlertSink(tray));
            sinks.Add(new RsiWatcherGUI.Core.TelegramAlertSink(_http, () => ("", "")));
            _alerts = new RsiWatcherGUI.Core.CompositeAlertSink(sinks.ToArray());
        }

        async void StartStopBtn_Click(object? sender, EventArgs e)
        {
            if (running)
            {
                // Stop
                _cts?.Cancel();
                running = false;
                startStopBtn.Text = "Старт";
                statusLbl.Text = "Остановлено.";
                return;
            }

            try
            {
                statusLbl.Text = "Запуск мониторинга…";
                var root = ((ComboItem)instrumentCombo.SelectedItem!).Tag;

                var frames = new Dictionary<Timeframe, TfConfig>();
                frames[Timeframe.M5] = new TfConfig((int)periodUp.Value, (double)obUp.Value, (double)osUp.Value, true);
                frames[Timeframe.M15] = new TfConfig((int)periodUp.Value, (double)obUp.Value, (double)osUp.Value, true);
                frames[Timeframe.M30] = new TfConfig((int)periodUp.Value, (double)obUp.Value, (double)osUp.Value, true);

                var settings = new RsiMonitorSettings(root, string.IsNullOrWhiteSpace(manualSecidBox.Text) ? null : manualSecidBox.Text.Trim(), autoFrontCheck.Checked, (int)pollUp.Value, frames);

                _cts = new System.Threading.CancellationTokenSource();
                running = true; startStopBtn.Text = "Стоп"; statusLbl.Text = "Работает.";

                // Start monitor in background
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await (_monitor?.StartAsync(settings, _cts.Token) ?? Task.CompletedTask);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        Log("Фатальная ошибка: " + ex.Message);
                    }
                    finally
                    {
                        this.InvokeIfRequired(() => { running = false; startStopBtn.Text = "Старт"; statusLbl.Text = "Остановлено."; });
                    }
                });
            }
            catch (Exception ex)
            {
                statusLbl.Text = "Ошибка.";
                Log("Ошибка запуска: " + ex.Message);
                MessageBox.Show(ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        async Task TickOnceAsync()
        {
            if (!running || string.IsNullOrWhiteSpace(resolvedSecid)) return;

            try
            {
                var engine = new RsiEngine(_http);
                var candles1m = await engine.FetchCandles1mAsync(resolvedSecid!, limit: 6000);

                var c5 = RsiEngine.Aggregate(candles1m, TimeSpan.FromMinutes(5));
                var c15 = RsiEngine.Aggregate(candles1m, TimeSpan.FromMinutes(15));
                var c30 = RsiEngine.Aggregate(candles1m, TimeSpan.FromMinutes(30));

                var rsi5 = RsiEngine.ComputeRsi(c5.Select(x => x.Close).ToList(), (int)periodUp.Value);
                var rsi15 = RsiEngine.ComputeRsi(c15.Select(x => x.Close).ToList(), (int)periodUp.Value);
                var rsi30 = RsiEngine.ComputeRsi(c30.Select(x => x.Close).ToList(), (int)periodUp.Value);

                rsiLbl.Text = $"RSI 5/15/30: {Fmt(rsi5)}/{Fmt(rsi15)}/{Fmt(rsi30)}";

                var last = new[] { c5.LastOrDefault(), c15.LastOrDefault(), c30.LastOrDefault() }.FirstOrDefault(x => x != null);
                if (last != null) statusLbl.Text = $"last close={last!.Close:F2}";

                CheckZone(5, rsi5);
                CheckZone(15, rsi15);
                CheckZone(30, rsi30);
            }
            catch (Exception ex)
            {
                Log("Ошибка цикла: " + ex.Message);
            }
        }

        void CheckZone(int tf, double? rsi)
        {
            if (rsi is null) return;
            var val = rsi.Value;
            var ob = (double)obUp.Value;
            var os = (double)osUp.Value;

            string key = $"TF{tf}m";

            if (val >= ob && zones[tf] != Zone.Overbought)
            {
                zones[tf] = Zone.Overbought;
                var msg = $"{resolvedSecid} {key}: Перекуплен (RSI={val:F2} ≥ {ob})";
                Log("⚠ " + msg);
                Alert(msg);
                SetIndicatorAlert();
            }
            else if (val <= os && zones[tf] != Zone.Oversold)
            {
                zones[tf] = Zone.Oversold;
                var msg = $"{resolvedSecid} {key}: Перепродан (RSI={val:F2} ≤ {os})";
                Log("⚠ " + msg);
                Alert(msg);
                SetIndicatorAlert();
            }
            else if (val < ob && val > os && zones[tf] != Zone.Neutral)
            {
                zones[tf] = Zone.Neutral;
                Log($"{resolvedSecid} {key}: Возврат в диапазон ({os}..{ob})");
                SetIndicatorNormal();
            }
        }

        void SetIndicatorAlert()
        {
            try
            {
                this.InvokeIfRequired(() =>
                {
                    statusIndicator.BackColor = Color.Red;
                    rsiLbl.ForeColor = Color.Red;
                    _blinkTimer.Start();
                });
            }
            catch { }
        }

        void SetIndicatorNormal()
        {
            try
            {
                this.InvokeIfRequired(() =>
                {
                    // Only clear alert state if all timeframes are neutral. If any timeframe
                    // is still Overbought/Oversold, keep the alert state.
                    if (zones.Values.Any(z => z != Zone.Neutral))
                    {
                        statusIndicator.BackColor = Color.Red;
                        rsiLbl.ForeColor = Color.Red;
                        if (!_blinkTimer.Enabled) _blinkTimer.Start();
                    }
                    else
                    {
                        _blinkTimer.Stop();
                        statusIndicator.BackColor = Color.Green;
                        statusIndicator.Visible = true;
                        rsiLbl.ForeColor = Color.Green;
                    }
                });
            }
            catch { }
        }

        void ToggleIndicator()
        {
            try { this.InvokeIfRequired(() => { statusIndicator.Visible = !statusIndicator.Visible; }); } catch { }
        }

        void Alert(string message)
        {
            try
            {
                _alertCts?.Cancel();
                _alertCts = new System.Threading.CancellationTokenSource();
                _ = (_alerts?.NotifyAsync(message, _alertCts.Token) ?? Task.CompletedTask);
            }
            catch { }
        }

        void Log(string msg)
        {
            logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
            logBox.SelectionStart = logBox.TextLength;
            logBox.ScrollToCaret();
        }

        enum Zone { Neutral, Overbought, Oversold }

        class ComboItem
        {
            public string Text { get; }
            public string Tag { get; }
            public ComboItem(string text, string tag) { Text = text; Tag = tag; }
            public override string ToString() => Text;
        }

        private sealed class UiLogger : RsiWatcherGUI.Core.ILogger
        {
            private readonly MainForm _owner;
            public UiLogger(MainForm owner) => _owner = owner;
            public void Info(string message)
            {
                _owner.InvokeIfRequired(() => _owner.Log(message));
                if (message.StartsWith("SECID:", StringComparison.OrdinalIgnoreCase))
                    _owner.InvokeIfRequired(() => _owner.secidLbl.Text = message.Substring(6).Trim());
                if (message.Contains("Перекуплен") || message.Contains("Перепродан"))
                    _owner.SetIndicatorAlert();
                if (message.Contains("Возврат в диапазон"))
                    _owner.SetIndicatorNormal();
            }
            public void Error(string message) => _owner.InvokeIfRequired(() => _owner.Log("ОШИБКА: " + message));
        }
    }
}
