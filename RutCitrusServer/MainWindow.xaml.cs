using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace RutCitrusServer
{
    public partial class MainWindow : FluentWindow
    {
        public ObservableCollection<ServerInfo> Servers { get; } = new ObservableCollection<ServerInfo>();
        private ServerInfo? _currentServer;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            Servers.CollectionChanged += (s, e) => UpdateEmptyText();
            UpdateEmptyText();

            Connector.OnConnected += () => Dispatcher.Invoke(() =>
            {
                StatusText.Text = "已连接";
                AppendLog("已连接到服务器");
                if (_currentServer != null)
                {
                    _currentServer.SetState(ConnectionState.Connected);
                }
            });

            Connector.OnDisconnected += () => Dispatcher.Invoke(() =>
            {
                StatusText.Text = "已断开";
                AppendLog("已断开与服务器的连接");
                if (_currentServer != null)
                {
                    _currentServer.SetState(ConnectionState.Disconnected);
                    _currentServer = null;
                }
            });

            Connector.OnMessageReceived += (message) => Dispatcher.Invoke(() =>
            {
                AppendLog(message);
            });
        }

        private void UpdateEmptyText()
        {
            EmptyServerText.Visibility = Servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AppendLog(string message)
        {
            LogTextBox.AppendText($"[{System.DateTime.Now:HH:mm:ss}] {message}\n");
            LogTextBox.ScrollToEnd();
        }

        private void HomeButton_Click(object sender, RoutedEventArgs e) => ShowPanel(HomePanel);
        private void FeatureButton_Click(object sender, RoutedEventArgs e) => ShowPanel(FeaturePanel);
        private void ExtensionButton_Click(object sender, RoutedEventArgs e) => ShowPanel(ExtensionPanel);
        private void SettingButton_Click(object sender, RoutedEventArgs e) => ShowPanel(SettingPanel);

        private void ShowPanel(UIElement panel)
        {
            HomePanel.Visibility = Visibility.Collapsed;
            FeaturePanel.Visibility = Visibility.Collapsed;
            ExtensionPanel.Visibility = Visibility.Collapsed;
            SettingPanel.Visibility = Visibility.Collapsed;
            panel.Visibility = Visibility.Visible;
        }

        private void AddServerButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new AddServerDialog();
            if (dialog.ShowDialog() == true)
            {
                var newName = dialog.ServerName;
                var newIP = dialog.ServerIP;
                var newPort = dialog.ServerPort;

                if (Servers.Any(s => s.Name == newName))
                {
                    System.Windows.MessageBox.Show($"已存在名为 \"{newName}\" 的服务器", "名称重复");
                    return;
                }

                if (Servers.Any(s => s.IP == newIP && s.Port == newPort))
                {
                    System.Windows.MessageBox.Show($"已存在地址为 {newIP}:{newPort} 的服务器", "地址重复");
                    return;
                }

                var server = new ServerInfo
                {
                    Name = newName,
                    IP = newIP,
                    Port = newPort
                };
                Servers.Add(server);
                AppendLog($"已添加服务器: {server.Name} ({server.IP}:{server.Port})");
            }
        }

        private void ConnectServerButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button && button.Tag is ServerInfo server)
            {
                switch (server.State)
                {
                    case ConnectionState.Disconnected:
                        _currentServer = server;
                        server.SetState(ConnectionState.Connecting);
                        _ = Connector.ConnectAsync(server.IP, server.Port);
                        AppendLog($"正在连接服务器: {server.Name} ({server.IP}:{server.Port})");
                        break;
                    case ConnectionState.Connecting:
                        break;
                    case ConnectionState.Connected:
                        server.SetState(ConnectionState.Disconnected);
                        _ = Connector.DisconnectAsync();
                        AppendLog($"断开连接服务器: {server.Name}");
                        break;
                }
            }
        }

        private void DeleteServerButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button && button.Tag is ServerInfo server)
            {
                if (server.State == ConnectionState.Connected || server.State == ConnectionState.Connecting)
                {
                    _ = Connector.DisconnectAsync();
                }
                Servers.Remove(server);
                AppendLog($"已删除服务器: {server.Name}");
            }
        }
    }

    public enum ConnectionState
    {
        Disconnected,
        Connecting,
        Connected
    }

    public class ServerInfo : INotifyPropertyChanged
    {
        private ConnectionState _state = ConnectionState.Disconnected;

        public string Name { get; set; } = "";
        public string IP { get; set; } = "";
        public int Port { get; set; } = 7789;
        public string DisplayEndpoint => $"{IP}:{Port}";

        public ConnectionState State
        {
            get => _state;
            private set
            {
                if (_state != value)
                {
                    _state = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ButtonText));
                    OnPropertyChanged(nameof(CanConnect));
                    OnPropertyChanged(nameof(ButtonAppearance));
                }
            }
        }

        public string ButtonText => State switch
        {
            ConnectionState.Disconnected => "连接",
            ConnectionState.Connecting => "连接中",
            ConnectionState.Connected => "断开连接",
            _ => "连接"
        };

        public bool CanConnect => State != ConnectionState.Connecting;

        public ControlAppearance ButtonAppearance => State switch
        {
            ConnectionState.Disconnected => ControlAppearance.Primary,
            ConnectionState.Connecting => ControlAppearance.Secondary,
            ConnectionState.Connected => ControlAppearance.Danger,
            _ => ControlAppearance.Primary
        };

        public void SetState(ConnectionState state)
        {
            State = state;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
