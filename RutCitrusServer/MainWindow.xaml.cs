using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Newtonsoft.Json.Linq;

namespace RutCitrusServer
{
    public partial class MainWindow : FluentWindow
    {
        public ObservableCollection<ServerInfo> Servers { get; } = new ObservableCollection<ServerInfo>();
        public ObservableCollection<ExtensionInfo> Extensions { get; } = new ObservableCollection<ExtensionInfo>();
        private ServerInfo? _currentServer;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            Servers.CollectionChanged += (s, e) => UpdateEmptyText();
            Extensions.CollectionChanged += (s, e) => UpdateExtensionEmptyText();
            UpdateEmptyText();
            UpdateExtensionEmptyText();

            Connector.OnConnected += () => Dispatcher.Invoke(() =>
            {
                StatusText.Text = "已连接";
                AppendLog("已连接到服务器");
                if (_currentServer != null)
                {
                    _currentServer.SetState(ConnectionState.Connected);
                }
                UpdateExtensionConnectionStatus();
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
                Extensions.Clear();
                UpdateExtensionConnectionStatus();
            });

            Connector.OnMessageReceived += (message) => Dispatcher.Invoke(() =>
            {
                AppendLog(message);
            });

            Connector.OnExtensionListReceived += (json) => Dispatcher.Invoke(() =>
            {
                ParseExtensionList(json);
            });

            Connector.OnExtensionUnloadResult += (success, extensionKey) => Dispatcher.Invoke(() =>
            {
                if (success)
                {
                    var ext = Extensions.FirstOrDefault(e => e.Key == extensionKey);
                    if (ext != null)
                    {
                        Extensions.Remove(ext);
                        AppendLog($"扩展 {ext.Name} 已卸载");
                    }
                }
                else
                {
                    AppendLog($"扩展 {extensionKey} 卸载失败");
                }
            });
        }

        private void UpdateEmptyText()
        {
            EmptyServerText.Visibility = Servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateExtensionEmptyText()
        {
            EmptyExtensionText.Visibility = Extensions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateExtensionConnectionStatus()
        {
            ExtensionNotConnectedText.Visibility = Connector.IsConnected ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ParseExtensionList(string json)
        {
            try
            {
                Extensions.Clear();
                var array = JArray.Parse(json);
                foreach (var item in array)
                {
                    var ext = new ExtensionInfo
                    {
                        Key = item["Key"]?.ToString() ?? "",
                        Name = item["Name"]?.ToString() ?? "未知",
                        Version = item["Version"]?.ToString() ?? "未知",
                        Description = item["Description"]?.ToString() ?? "无描述",
                        LoadTime = item["LoadTime"]?.ToString() ?? ""
                    };
                    Extensions.Add(ext);
                }
                AppendLog($"已获取 {Extensions.Count} 个扩展信息");
            }
            catch (Exception ex)
            {
                AppendLog($"解析扩展列表失败: {ex.Message}");
            }
        }

        private void AppendLog(string message)
        {
            LogTextBox.AppendText($"[{System.DateTime.Now:HH:mm:ss}] {message}\n");
            LogTextBox.ScrollToEnd();
        }

        private void HomeButton_Click(object sender, RoutedEventArgs e) => ShowPanel(HomePanel);
        private void FeatureButton_Click(object sender, RoutedEventArgs e) => ShowPanel(FeaturePanel);
        private void ExtensionButton_Click(object sender, RoutedEventArgs e)
        {
            ShowPanel(ExtensionPanel);
            UpdateExtensionConnectionStatus();
            if (Connector.IsConnected)
            {
                _ = Connector.RequestExtensionsAsync();
            }
        }
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

        private void RefreshExtensionsButton_Click(object sender, RoutedEventArgs e)
        {
            if (Connector.IsConnected)
            {
                _ = Connector.RequestExtensionsAsync();
            }
            else
            {
                AppendLog("请先连接到服务器");
            }
        }

        private void UnloadExtensionButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button && button.Tag is string extensionKey)
            {
                var ext = Extensions.FirstOrDefault(e => e.Key == extensionKey);
                if (ext != null)
                {
                    var result = System.Windows.MessageBox.Show(
                        $"确定要卸载扩展 \"{ext.Name}\" 吗？\n\n此操作将卸载服务器上的扩展。",
                        "确认卸载",
                        System.Windows.MessageBoxButton.YesNo,
                        System.Windows.MessageBoxImage.Warning);

                    if (result == System.Windows.MessageBoxResult.Yes)
                    {
                        _ = Connector.UnloadExtensionAsync(extensionKey);
                        AppendLog($"正在请求卸载扩展: {ext.Name}");
                    }
                }
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

    public class ExtensionInfo
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public string Description { get; set; } = "";
        public string LoadTime { get; set; } = "";
    }
}
