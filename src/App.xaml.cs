using System.IO;
using System.Windows;

namespace WinMigrationCleaner
{
    public partial class App : Application
    {
        static bool _crashShown;

        protected override void OnStartup(StartupEventArgs e)
        {
            // pnputil / WMI output on Chinese Windows uses codepage 936 (GBK)
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            DispatcherUnhandledException += (s, args) =>
            {
                TryWriteCrash(args.Exception);
                if (!_crashShown)
                {
                    _crashShown = true;
                    MessageBox.Show($"发生错误：{args.Exception.Message}", "WinMigrationCleaner",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    _crashShown = false;
                }
                args.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
                TryWriteCrash(args.ExceptionObject as Exception);

            base.OnStartup(e);
        }

        static void TryWriteCrash(Exception? ex)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WinMigrationCleaner");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {ex}\r\n\r\n");
            }
            catch { }
        }
    }
}
