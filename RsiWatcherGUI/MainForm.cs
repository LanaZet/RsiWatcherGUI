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
    private Task? _monitorTask;

        public MainForm()
        {
            Text = "RSI Watcher (MOEX Futures) — WinForms";
            MinimumSize = new Size(800, 540);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("rsi-watcher-winforms/1.0");
            FormClosing += MainForm_FormClosing;

            // ...existing code...
            _timer.Tick += (_, __) => _ = TickOnceAsync();

            // Верхняя панель
            var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 120, ColumnCount = 1, RowCount = 3 };
            top.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            top.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            top.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(top);

            var row1 = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
            row1.Controls.Add(new Label { Text = "Инструмент:", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft });
            foreach (var it in RsiWatcherGUI.Shared.InstrumentCatalog.Items)
                instrumentCombo.Items.Add(new ComboItem(it.Display, it.Tag));
            instrumentCombo.SelectedIndex = 0;
            instrumentCombo.SelectedIndexChanged += InstrumentCombo_SelectedIndexChanged;
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


            _blinkTimer.Tick += (s, e) => ToggleIndicator();

            // Compose alerts using available sinks
            var sinks = new List<RsiWatcherGUI.Core.IAlertSink>();
            if (beepCheck.Checked) sinks.Add(new RsiWatcherGUI.Core.BeepAlertSink());
            if (balloonCheck.Checked) sinks.Add(new RsiWatcherGUI.Core.TrayAlertSink(tray));
            sinks.Add(new RsiWatcherGUI.Core.TelegramAlertSink(_http, () => ("", "")));
            _alerts = new RsiWatcherGUI.Core.CompositeAlertSink(sinks.ToArray());

            // create monitor after alerts are composed so it receives the correct sinks
            _monitor = new RsiMonitor(moex, agg, rsi, resolver, _alerts, time, new UiLogger(this));
        }

        private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            try
            {
                // Stop timers
                _timer.Stop();
                _blinkTimer.Stop();

                // Cancel monitor and alert tasks
                try { _cts?.Cancel(); } catch { }
                try { _alertCts?.Cancel(); } catch { }

                // If a monitor task is running, wait a short time for it to finish
                if (_monitorTask != null)
                {
                    try
                    {
                        var t = _monitorTask;
                        if (!t.Wait(TimeSpan.FromSeconds(2)))
                        {
                            // still running; attempt to cancel again and continue shutdown
                            try { _cts?.Cancel(); } catch { }
                        }
                    }
                    catch { }
                }

                // Dispose monitor if it implements IDisposable (best-effort)
                try { (_monitor as IDisposable)?.Dispose(); } catch { }

                // Dispose alert sinks if they implement IDisposable
                try { (_alerts as IDisposable)?.Dispose(); } catch { }

                // Dispose tray icon
                try { tray.Visible = false; tray.Dispose(); } catch { }
            }
            catch { }
        }

        private void Monitor_StateChanged(string secid, Timeframe tf, double? rsi, Zone zone)
        {
            // Update resolved secid
            // Use UI thread for label updates and alert logic
            try
            {
                this.InvokeIfRequired(() =>
                {
                    // resolved secid
                    secidLbl.Text = secid; resolvedSecid = secid;

                    // fetch current values from rsiLbl and replace the corresponding tf
                    var parts = rsiLbl.Text.Split(':');
                    var label = parts[0];
                    var values = parts.Length > 1 ? parts[1].Trim() : "—/—/—";
                    var arr = values.Split('/').Select(s => s.Trim()).ToArray();
                    string f5 = arr.Length > 0 ? arr[0] : "—";
                    string f15 = arr.Length > 1 ? arr[1] : "—";
                    string f30 = arr.Length > 2 ? arr[2] : "—";

                    var newVal = rsi.HasValue ? rsi.Value.ToString("F1") : "—";
                    if (tf == Timeframe.M5) f5 = newVal;
                    else if (tf == Timeframe.M15) f15 = newVal;
                    else if (tf == Timeframe.M30) f30 = newVal;

                    rsiLbl.Text = $"{label}: {f5}/{f15}/{f30}";

                    // Zone transition + alerts: compare previous zone and trigger alerts only on change
                    int key = (int)tf;
                    var prev = zones.ContainsKey(key) ? zones[key] : Zone.Neutral;
                    if (zone != prev)
                    {
                        zones[key] = zone;
                        var ob = (double)obUp.Value; var os = (double)osUp.Value;
                        string keyLabel = $"TF{key}m";
                        if (zone == Zone.Overbought)
                        {
                            var msg = $"{secid} {keyLabel}: Перекуплен (RSI={rsi:F2} ≥ {ob})";
                            Log("⚠ " + msg);
                            Alert(msg);
                            SetIndicatorAlert();
                        }
                        else if (zone == Zone.Oversold)
                        {
                            var msg = $"{secid} {keyLabel}: Перепродан (RSI={rsi:F2} ≤ {os})";
                            Log("⚠ " + msg);
                            Alert(msg);
                            SetIndicatorAlert();
                        }
                        else // neutral
                        {
                            Log($"{secid} {keyLabel}: Возврат в диапазон ({os}..{ob})");
                            // If all neutral, clear indicator; otherwise keep alert state
                            if (zones.Values.All(z => z == Zone.Neutral)) SetIndicatorNormal();
                            else SetIndicatorAlert();
                        }
                    }
                    else
                    {
                        // zone unchanged: ensure indicator state is consistent
                        if (zones.Values.All(z => z == Zone.Neutral)) SetIndicatorNormal();
                    }
                });
            }
            catch { }
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
                    var root = ((ComboItem)instrumentCombo.SelectedItem!).Tag;
                    var frames = new Dictionary<Timeframe, TfConfig>();
                    frames[Timeframe.M5] = new TfConfig((int)periodUp.Value, (double)obUp.Value, (double)osUp.Value, true);
                    frames[Timeframe.M15] = new TfConfig((int)periodUp.Value, (double)obUp.Value, (double)osUp.Value, true);
                    frames[Timeframe.M30] = new TfConfig((int)periodUp.Value, (double)obUp.Value, (double)osUp.Value, true);

                    var settings = new RsiMonitorSettings(root, string.IsNullOrWhiteSpace(manualSecidBox.Text) ? null : manualSecidBox.Text.Trim(), autoFrontCheck.Checked, (int)pollUp.Value, frames);
                    await StartMonitor(settings);
                }
            catch (Exception ex)
            {
                statusLbl.Text = "Ошибка.";
                Log("Ошибка запуска: " + ex.Message);
                MessageBox.Show(ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task StartMonitor(RsiMonitorSettings settings)
        {
            try
            {
                statusLbl.Text = "Запуск мониторинга…";
                _cts = new System.Threading.CancellationTokenSource();
                running = true; startStopBtn.Text = "Стоп"; statusLbl.Text = "Работает.";

                // Resolve SECID immediately so the user sees what we're monitoring
                try
                {
                    var moex = new MoexIssClient(_http);
                    var resolver = new FortsFrontResolver(moex);
                    var sec = await resolver.ResolveActiveAsync(settings.InstrumentRoot, System.Threading.CancellationToken.None);
                    this.InvokeIfRequired(() => { secidLbl.Text = sec; resolvedSecid = sec; });
                }
                catch { }

                // Kick a single immediate fetch to update RSI displays right away (doesn't block monitor startup)
                _ = Task.Run(async () => { try { await TickOnceAsync(); } catch { } });

                // Subscribe to structured state events
                try { _monitor.StateChanged += Monitor_StateChanged; } catch { }

                _monitorTask = Task.Run(async () =>
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
                        try { _monitor.StateChanged -= Monitor_StateChanged; } catch { }
                        _monitorTask = null;
                    }
                });
            }
            catch (Exception ex)
            {
                Log("Ошибка запуска: " + ex.Message);
            }
        }

        private async void InstrumentCombo_SelectedIndexChanged(object? sender, EventArgs e)
        {
            var root = ((ComboItem)instrumentCombo.SelectedItem!).Tag;

            // If not running, resolve SECID to show the user
            if (!running)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var moex = new MoexIssClient(_http);
                        var resolver = new FortsFrontResolver(moex);
                        var sec = await resolver.ResolveActiveAsync(root, System.Threading.CancellationToken.None);
                        this.InvokeIfRequired(() => { secidLbl.Text = sec; resolvedSecid = sec; });
                            // fetch RSI once for the newly selected secid so UI is up-to-date
                            try { await FetchAndUpdateAsync(sec); } catch { }
                    }
                    catch { }
                });
                return;
            }

            // If running, restart monitor with new root
            try
            {
                statusLbl.Text = "Переключение инструмента…";
                _cts?.Cancel();
                if (_monitorTask != null)
                {
                    try { await Task.Run(() => _monitorTask!.Wait(2000)); } catch { }
                }

                var frames = new Dictionary<Timeframe, TfConfig>();
                frames[Timeframe.M5] = new TfConfig((int)periodUp.Value, (double)obUp.Value, (double)osUp.Value, true);
                frames[Timeframe.M15] = new TfConfig((int)periodUp.Value, (double)obUp.Value, (double)osUp.Value, true);
                frames[Timeframe.M30] = new TfConfig((int)periodUp.Value, (double)obUp.Value, (double)osUp.Value, true);

                var settings = new RsiMonitorSettings(root, string.IsNullOrWhiteSpace(manualSecidBox.Text) ? null : manualSecidBox.Text.Trim(), autoFrontCheck.Checked, (int)pollUp.Value, frames);
                await StartMonitor(settings);
            }
            catch (Exception ex)
            {
                Log("Ошибка переключения: " + ex.Message);
            }
        }

        async Task TickOnceAsync()
        {
            if (string.IsNullOrWhiteSpace(resolvedSecid)) return;
            await FetchAndUpdateAsync(resolvedSecid!);
        }

        // Perform a one-shot fetch and update UI for the provided SECID. This is used when
        // switching instruments while the monitor is not running or to provide an immediate
        // snapshot while starting the monitor.
        async Task FetchAndUpdateAsync(string secid)
        {
            try
            {
                // show transient status while we fetch
                string prevStatus = statusLbl.Text;
                this.InvokeIfRequired(() => statusLbl.Text = "Обновление…");

                var engine = new RsiEngine(_http);
                var candles1m = await engine.FetchCandles1mAsync(secid, limit: 6000);

                var c5 = RsiEngine.Aggregate(candles1m, TimeSpan.FromMinutes(5));
                var c15 = RsiEngine.Aggregate(candles1m, TimeSpan.FromMinutes(15));
                var c30 = RsiEngine.Aggregate(candles1m, TimeSpan.FromMinutes(30));

                var rsi5 = RsiEngine.ComputeRsi(c5.Select(x => x.Close).ToList(), (int)periodUp.Value);
                var rsi15 = RsiEngine.ComputeRsi(c15.Select(x => x.Close).ToList(), (int)periodUp.Value);
                var rsi30 = RsiEngine.ComputeRsi(c30.Select(x => x.Close).ToList(), (int)periodUp.Value);

                // Instead of updating UI directly, route updates through the same
                // Monitor_StateChanged pathway used by the running monitor so both
                // one-shot and periodic updates follow a single codepath.
                Zone z5 = Zone.Neutral; Zone z15 = Zone.Neutral; Zone z30 = Zone.Neutral;
                var ob = (double)obUp.Value; var os = (double)osUp.Value;
                if (rsi5.HasValue)
                {
                    if (rsi5.Value >= ob) z5 = Zone.Overbought;
                    else if (rsi5.Value <= os) z5 = Zone.Oversold;
                }
                if (rsi15.HasValue)
                {
                    if (rsi15.Value >= ob) z15 = Zone.Overbought;
                    else if (rsi15.Value <= os) z15 = Zone.Oversold;
                }
                if (rsi30.HasValue)
                {
                    if (rsi30.Value >= ob) z30 = Zone.Overbought;
                    else if (rsi30.Value <= os) z30 = Zone.Oversold;
                }

                // Fire UI updates via the existing handler (it will marshal to UI thread as needed)
                Monitor_StateChanged(secid, Timeframe.M5, rsi5, z5);
                Monitor_StateChanged(secid, Timeframe.M15, rsi15, z15);
                Monitor_StateChanged(secid, Timeframe.M30, rsi30, z30);

                // Alerts and zone transitions are handled via Monitor_StateChanged now.

                var last = new[] { c5.LastOrDefault(), c15.LastOrDefault(), c30.LastOrDefault() }.FirstOrDefault(x => x != null);
                if (last != null) this.InvokeIfRequired(() => statusLbl.Text = $"last close={last!.Close:F2}");
                // restore previous status
                this.InvokeIfRequired(() => statusLbl.Text = prevStatus);
            }
            catch (Exception ex)
            {
                Log("Ошибка цикла: " + ex.Message);
                this.InvokeIfRequired(() => statusLbl.Text = "Ошибка обновления");
            }
        }

        // CheckZone removed — zone transitions and alerts are now handled centrally in Monitor_StateChanged

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

                // SECID explicit message
                if (message.StartsWith("SECID:", StringComparison.OrdinalIgnoreCase))
                {
                    _owner.InvokeIfRequired(() => { var s = message.Substring(6).Trim(); _owner.secidLbl.Text = s; _owner.resolvedSecid = s; });
                    return;
                }

                // Legacy periodic summary messages are no longer parsed here.
                // Structured updates now arrive via RsiMonitor.StateChanged and update the UI directly.

                // Alert messages
                if (message.Contains("Перекуплен") || message.Contains("Перепродан"))
                    _owner.SetIndicatorAlert();
                if (message.Contains("Возврат в диапазон"))
                    _owner.SetIndicatorNormal();
            }
            public void Error(string message) => _owner.InvokeIfRequired(() => _owner.Log("ОШИБКА: " + message));
        }
    }
}
