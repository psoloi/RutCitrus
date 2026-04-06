using System.Windows;
using Wpf.Ui.Controls;

namespace RutCitrusServer
{
    public partial class AddServerDialog : FluentWindow
    {
        public string ServerName => NameTextBox.Text.Trim();
        public string ServerIP => IPTextBox.Text.Trim();
        public int ServerPort => int.TryParse(PortTextBox.Text.Trim(), out int port) ? port : 7789;
        public string ServerKey => KeyPasswordBox.Password;

        public AddServerDialog()
        {
            InitializeComponent();
            NameTextBox.Focus();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ServerName))
            {
                System.Windows.MessageBox.Show("请输入服务器名称", "提示", System.Windows.MessageBoxButton.OK);
                NameTextBox.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(ServerIP))
            {
                System.Windows.MessageBox.Show("请输入IP地址", "提示", System.Windows.MessageBoxButton.OK);
                IPTextBox.Focus();
                return;
            }

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
