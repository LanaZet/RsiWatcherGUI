using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

public class MainForm : Form
{
    // Источник данных: фьючерсы на MOEX (FORTS)
    const string Engine = "futures";
    const string Market = "forts";
    const string Board = "RFUD";
    const int Interval1m = 1;

    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 30000 }; // обновляется из UI

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
    Label rsiLbl = new() { AutoSize = true, Text = "RSI 5/15/30: —" };
    TextBox logBox = new() { Multiline = true, ScrollBars = ScrollBars.Both, ReadOnly = true, WordWrap = true, Dock = DockStyle.Fill };

    NotifyIcon tray = new() { Visible = true, Icon = SystemIcons.Information, Text = "RSI Alerts (MOEX)" };

    bool running = false;
    string? resolvedSecid;
    readonly Dictionary<int, Zone> zones = new() { { 5, Zone.Neutral }, { 15, Zone.Neutral }, { 30, Zone.Neutral } };

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
        row3.Controls.Add(new Label { Text = "   ", AutoSize = true });
        row3.Controls.Add(secidLbl);
        row3.Controls.Add(new Label { Text = "   ", AutoSize = true });
        row3.Controls.Add(rsiLbl);

        top.Controls.Add(row1);
        top.Controls.Add(row2);
        top.Controls.Add(row3);

        // Лог
        Controls.Add(logBox);
    }

    async void StartStopBtn_Click(object? sender, EventArgs e)
    {
        if (running)
        {
            running = false;
            _timer.Stop();
            startStopBtn.Text = "Старт";
            statusLbl.Text = "Остановлено.";
            return;
        }

        try
        {
            statusLbl.Text = "Определяю контракт…";
            var root = ((ComboItem)instrumentCombo.SelectedItem!).Tag;

            if (autoFrontCheck.Checked)
                resolvedSecid = await ResolveActiveContractAsync(root);
            else
                resolvedSecid = string.IsNullOrWhiteSpace(manualSecidBox.Text) ? throw new Exception("Укажите SECID или включите авто-контракт.") : manualSecidBox.Text.Trim();

            secidLbl.Text = $"SECID: {resolvedSecid}";
            zones[5] = zones[15] = zones[30] = Zone.Neutral;

            _timer.Interval = (int)pollUp.Value * 1000;
            running = true;
            startStopBtn.Text = "Стоп";
            statusLbl.Text = "Работает.";
            await TickOnceAsync();
            _timer.Start();
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
            var candles1m = await FetchCandles1mAsync(resolvedSecid!, limit: 6000);

            var c5 = Aggregate(candles1m, TimeSpan.FromMinutes(5));
            var c15 = Aggregate(candles1m, TimeSpan.FromMinutes(15));
            var c30 = Aggregate(candles1m, TimeSpan.FromMinutes(30));

            var rsi5 = ComputeRsi(c5.Select(x => x.Close).ToList(), (int)periodUp.Value);
            var rsi15 = ComputeRsi(c15.Select(x => x.Close).ToList(), (int)periodUp.Value);
            var rsi30 = ComputeRsi(c30.Select(x => x.Close).ToList(), (int)periodUp.Value);

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
        }
        else if (val <= os && zones[tf] != Zone.Oversold)
        {
            zones[tf] = Zone.Oversold;
            var msg = $"{resolvedSecid} {key}: Перепродан (RSI={val:F2} ≤ {os})";
            Log("⚠ " + msg);
            Alert(msg);
        }
        else if (val < ob && val > os && zones[tf] != Zone.Neutral)
        {
            zones[tf] = Zone.Neutral;
            Log($"{resolvedSecid} {key}: Возврат в диапазон ({os}..{ob})");
        }
    }

    void Alert(string message)
    {
        if (beepCheck.Checked) { try { System.Media.SystemSounds.Exclamation.Play(); } catch { } }
        if (balloonCheck.Checked)
        {
            try
            {
                tray.BalloonTipTitle = "RSI сигнал";
                tray.BalloonTipText = message;
                tray.ShowBalloonTip(3000);
            }
            catch { }
        }
    }

    // ===== Работа с MOEX ISS =====

    async Task<string> ResolveActiveContractAsync(string root)
    {
        var all = await ListSecuritiesAsync();
        var re = new Regex("^" + Regex.Escape(root) + "[FGHJKMNQUVXZ][0-9]$", RegexOptions.IgnoreCase);

        var candidates = all.Where(id => re.IsMatch(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (candidates.Count == 0)
            throw new Exception($"Не нашёл активные контракты для корня {root}.");

        DateTime best = DateTime.MinValue;
        string? bestSec = null;
        foreach (var sec in candidates)
        {
            try
            {
                var last = await FetchLastCandleEndAsync(sec, interval: 60);
                if (last > best) { best = last; bestSec = sec; }
            }
            catch { }
        }
        if (bestSec == null) throw new Exception($"Не удалось определить активный контракт для {root}.");
        Log($"Определён контракт: {bestSec}");
        return bestSec;
    }

    async Task<List<string>> ListSecuritiesAsync()
    {
        var url = $"https://iss.moex.com/iss/engines/{Engine}/markets/{Market}/boards/{Board}/securities.json";
        using var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonObject>();

        var node = json?["securities"]?.AsObject() ?? throw new Exception("Нет секции securities.");
        var cols = node["columns"]!.AsArray().Select(n => n!.ToString()).ToList();
        var data = node["data"]!.AsArray();

        int idxSecid = cols.FindIndex(c => c.Equals("SECID", StringComparison.OrdinalIgnoreCase));
        if (idxSecid < 0) throw new Exception("Поле SECID не найдено.");

        var list = new List<string>(data.Count);
        foreach (var row in data)
        {
            if (row is JsonArray arr && arr[idxSecid] != null)
                list.Add(arr[idxSecid]!.ToString());
        }
        return list;
    }

    async Task<DateTime> FetchLastCandleEndAsync(string secid, int interval)
    {
        var url = $"https://iss.moex.com/iss/engines/{Engine}/markets/{Market}/boards/{Board}/securities/{secid}/candles.json?interval={interval}&limit=1";
        using var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonObject>();

        var node = json?["candles"]?.AsObject() ?? throw new Exception("Нет секции candles");
        var cols = node["columns"]!.AsArray().Select(n => n!.ToString()).ToList();
        var data = node["data"]!.AsArray();
        if (data.Count == 0) return DateTime.MinValue;

        int idxEnd = cols.FindIndex(c => c.Equals("end", StringComparison.OrdinalIgnoreCase));
        var endStr = (data[0] as JsonArray)?[idxEnd]?.ToString() ?? "";
        return ParseIso(endStr);
    }

    async Task<List<Candle>> FetchCandles1mAsync(string secid, int limit)
    {
        var url = $"https://iss.moex.com/iss/engines/{Engine}/markets/{Market}/boards/{Board}/securities/{secid}/candles.json?interval={Interval1m}&limit={limit}";
        using var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<JsonObject>();
        var node = json?["candles"]?.AsObject() ?? throw new Exception("Нет секции candles.");

        var cols = node["columns"]!.AsArray().Select(n => n!.ToString()).ToList();
        var data = node["data"]!.AsArray();

        int idxBegin = cols.IndexOf("begin");
        int idxEnd = cols.IndexOf("end");
        int idxOpen = cols.IndexOf("open");
        int idxHigh = cols.IndexOf("high");
        int idxLow = cols.IndexOf("low");
        int idxClose = cols.IndexOf("close");
        int idxVol = cols.IndexOf("volume");

        var list = new List<Candle>(data.Count);
        foreach (var row in data)
        {
            if (row is not JsonArray arr) continue;
            var c = new Candle(
                Begin: ParseIso(arr[idxBegin]?.ToString()),
                End: ParseIso(arr[idxEnd]?.ToString()),
                Open: ParseDouble(arr[idxOpen]),
                High: ParseDouble(arr[idxHigh]),
                Low: ParseDouble(arr[idxLow]),
                Close: ParseDouble(arr[idxClose]),
                Volume: ParseLong(arr[idxVol])
            );
            list.Add(c);
        }
        list.Sort((a, b) => a.Begin.CompareTo(b.Begin));
        return list;
    }

    // ===== Агрегация и RSI =====
    static List<Candle> Aggregate(List<Candle> src, TimeSpan tf)
    {
        var buckets = new SortedDictionary<DateTime, List<Candle>>();
        foreach (var c in src)
        {
            var b = FloorToFrame(c.Begin, tf);
            if (!buckets.TryGetValue(b, out var list)) { list = new(); buckets[b] = list; }
            list.Add(c);
        }

        var result = new List<Candle>(buckets.Count);
        foreach (var kv in buckets)
        {
            var list = kv.Value;
            if (list.Count == 0) continue;
            list.Sort((a, b) => a.Begin.CompareTo(b.Begin));

            var begin = kv.Key;
            var end = begin + tf;

            // исключаем незавершённые
            if (end > DateTime.Now) continue;

            result.Add(new Candle(
                begin,
                end,
                Open: list.First().Open,
                High: list.Max(x => x.High),
                Low: list.Min(x => x.Low),
                Close: list.Last().Close,
                Volume: list.Sum(x => x.Volume)
            ));
        }
        return result;
    }

    static double? ComputeRsi(List<double> closes, int period)
    {
        if (closes == null || closes.Count < period + 1) return null;

        double gainSum = 0, lossSum = 0;
        for (int i = 1; i <= period; i++)
        {
            var ch = closes[i] - closes[i - 1];
            if (ch > 0) gainSum += ch; else lossSum += -ch;
        }
        double avgGain = gainSum / period;
        double avgLoss = lossSum / period;

        for (int i = period + 1; i < closes.Count; i++)
        {
            var ch = closes[i] - closes[i - 1];
            var gain = Math.Max(ch, 0);
            var loss = Math.Max(-ch, 0);
            avgGain = (avgGain * (period - 1) + gain) / period;
            avgLoss = (avgLoss * (period - 1) + loss) / period;
        }

        double rs = avgLoss == 0 ? double.PositiveInfinity : avgGain / avgLoss;
        return 100.0 - (100.0 / (1.0 + rs));
    }

    // ===== Модель и утилиты =====
    record Candle(DateTime Begin, DateTime End, double Open, double High, double Low, double Close, long Volume);

    static DateTime FloorToFrame(DateTime t, TimeSpan frame)
    {
        var ticks = frame.Ticks;
        return new DateTime((t.Ticks / ticks) * ticks, t.Kind);
    }

    static string Fmt(double? v) => v is null ? "n/a" : v.Value.ToString("F2");

    static DateTime ParseIso(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return DateTime.MinValue;
        return DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
    }
    static double ParseDouble(JsonNode? n)
        => double.Parse(n!.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture);
    static long ParseLong(JsonNode? n)
        => long.TryParse(n?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0L;

    enum Zone { Neutral, Overbought, Oversold }

    class ComboItem
    {
        public string Text { get; }
        public string Tag { get; }
        public ComboItem(string text, string tag) { Text = text; Tag = tag; }
        public override string ToString() => Text;
    }

    void Log(string msg)
    {
        logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
        logBox.SelectionStart = logBox.TextLength;
        logBox.ScrollToCaret();
    }
}
