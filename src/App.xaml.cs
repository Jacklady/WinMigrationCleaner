using System.IO;
using System.Windows;

namespace WinMigrationCleaner
{
    public partial class App : Application
    {
        static bool _crashShown;
        static readonly string PrefFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinMigrationCleaner", "theme.txt");

        public static string CurrentTheme { get; private set; } = "light";
        public static string? AutoScreenshot { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            // diagnostic: --screenshot <path> renders the window to PNG and exits
            if (e.Args.Length >= 2 && e.Args[0] == "--screenshot")
                AutoScreenshot = e.Args[1];
            // pnputil / WMI output on Chinese Windows uses codepage 936 (GBK)
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            // Load saved theme (default: light — maximum readability)
            try { if (File.Exists(PrefFile)) CurrentTheme = File.ReadAllText(PrefFile).Trim(); } catch { }
            ApplyTheme(CurrentTheme);

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

        public static void ApplyTheme(string theme)
        {
            CurrentTheme = theme == "light" ? "light" : "dark";
            var uri = new Uri($"Themes/{(CurrentTheme == "light" ? "Light" : "Dark")}.xaml", UriKind.Relative);
            var dict = new ResourceDictionary { Source = uri };
            Current.Resources.MergedDictionaries.Clear();
            Current.Resources.MergedDictionaries.Add(dict);
            try { Directory.CreateDirectory(Path.GetDirectoryName(PrefFile)!); File.WriteAllText(PrefFile, CurrentTheme); } catch { }
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
