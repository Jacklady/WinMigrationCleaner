using System.Windows;

namespace WinMigrationCleaner
{
    public partial class ConfirmDialog : Window
    {
        public bool Confirmed { get; private set; }

        public ConfirmDialog(string title, string message, string detail, string confirmText = "确认删除")
        {
            InitializeComponent();
            Owner = Application.Current.MainWindow;
            TitleText.Text = title;
            MessageText.Text = message;
            DetailText.Text = detail;
            BtnYes.Content = confirmText;
        }

        void BtnYes_Click(object sender, RoutedEventArgs e) { Confirmed = true; Close(); }
        void BtnNo_Click(object sender, RoutedEventArgs e) { Close(); }
    }
}
