using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace RutCitrusServer
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void HomeButton_Click(object sender, RoutedEventArgs e) => ShowPanel(HomePanel);
        private void FeatureButton_Click(object sender, RoutedEventArgs e) => ShowPanel(FeaturePanel);
        private void ExtensionButton_Click(object sender, RoutedEventArgs e) => ShowPanel(ExtensionPanel);
        private void SettingButton_Click(object sender, RoutedEventArgs e) => ShowPanel(SettingPanel);

        private void ShowPanel(StackPanel panel)
        {
            HomePanel.Visibility = Visibility.Collapsed;
            FeaturePanel.Visibility = Visibility.Collapsed;
            ExtensionPanel.Visibility = Visibility.Collapsed;
            SettingPanel.Visibility = Visibility.Collapsed;
            panel.Visibility = Visibility.Visible;
        }
    }
}