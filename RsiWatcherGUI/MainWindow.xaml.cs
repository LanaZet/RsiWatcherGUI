using System.Windows;
using System.Windows.Controls;
using RsiWatcherGUI.Core;
using System.Net.Http;
using MessageBox = System.Windows.Forms.MessageBox;
using TextBox = System.Windows.Controls.TextBox;

namespace RsiWatcherGUI
{
    public partial class MainWindow : Window
    {
    private NotifyIcon _tray;
    private IAutostartService _autostart;
    private MainViewModel _vm;

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

            // create viewmodel and set as DataContext
            _vm = new MainViewModel(_tray);
            DataContext = _vm;

            _vm.OnLog += (s) => Dispatcher.Invoke(() => { LogBox.AppendText(s + "\n"); LogBox.ScrollToEnd(); });

            // ManualSecid enablement handled by binding + converter
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

    // Window event handlers intentionally left out (no-op)
    }
}
