using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinMigrationCleaner
{
    public partial class MainWindow : Window
    {
        readonly ObservableCollection<LogEntry> _logs = new();
        CancellationTokenSource? _cts;
        bool _isAdmin;

        public MainWindow()
        {
            InitializeComponent();
            LogList.ItemsSource = _logs;
            Loaded += MainWindow_Loaded;
        }

        // ---------------- init & layout ----------------
        void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyAdminUi();
            BtnToggleTheme.Content = App.CurrentTheme == "light" ? "切换深色主题" : "切换浅色主题";

            Log("欢迎使用 WinMigrationCleaner", "ok");
            Log(_isAdmin ? "已以管理员身份运行" : "提示：清理操作需要管理员权限（右下角可一键提权重启）", "info");

            // hardware info (fast, sync ok)
            _hwRows = Scanner.GetHardwareInfo();
            InfoList.ItemsSource = _hwRows;

            // diagnostic self-screenshot mode
            if (App.AutoScreenshot != null)
            {
                var path = App.AutoScreenshot;
                var page = App.AutoScreenshotPage;
                Dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        if (page == "devices")
                        {
                            var devices = await Task.Run(Scanner.ScanAllDevices);
                            ListDevices.ItemsSource = new ObservableCollection<PhantomDevice>(devices);
                            UpdateDeviceMetrics(devices);
                            Nav_Click(NavDevices, null);
                        }
                        await Task.Delay(400);
                        var rtb = new RenderTargetBitmap(
                            (int)ActualWidth, (int)ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                        rtb.Render(this);
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(rtb));
                        using var fs = File.Create(path);
                        enc.Save(fs);
                    }
                    catch { }
                    finally { Application.Current.Shutdown(); }
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }

        List<HardwareRow>? _hwRows;

        void Nav_Click(object sender, RoutedEventArgs e)
        {
            var btn = (Button)sender;
            foreach (var b in new[] { NavDash, NavDevices, NavServices, NavDrivers, NavAbout })
                b.Tag = b == btn ? "active" : null;

            PageDash.Visibility = btn == NavDash ? Visibility.Visible : Visibility.Collapsed;
            PageDevices.Visibility = btn == NavDevices ? Visibility.Visible : Visibility.Collapsed;
            PageServices.Visibility = btn == NavServices ? Visibility.Visible : Visibility.Collapsed;
            PageDrivers.Visibility = btn == NavDrivers ? Visibility.Visible : Visibility.Collapsed;
            PageAbout.Visibility = btn == NavAbout ? Visibility.Visible : Visibility.Collapsed;
            Status($"已切换到「{StripIcon(btn.Content.ToString())}」");
        }

        static string StripIcon(string? s) => (s ?? "").Replace("⌂", "").Replace("▣", "")
            .Replace("⚙", "").Replace("⛃", "").Replace("ℹ", "").Trim();

        // ---------------- logging / status / busy ----------------
        void Log(string msg, string kind = "info")
        {
            _logs.Add(new LogEntry { Message = msg, Kind = kind });
            if (_logs.Count > 500) _logs.RemoveAt(0);
            LogList.ScrollIntoView(LogList.Items[^1]);
        }

        void Status(string s) => StatusText.Text = s;

        void SetBusy(bool busy, string? label = null, bool determinate = false)
        {
            MainProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            MainProgress.IsIndeterminate = !determinate;
            if (determinate) MainProgress.Value = 0;
            BtnBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            BtnBusy.Content = busy ? "■ 停止" : "■ 停止";
            foreach (var b in new Button?[] { BtnScanAll, BtnExportReport, BtnScanDevices, BtnRemoveDevices,
                     BtnScanServices, BtnDeleteServices, BtnDisableServices, BtnScanDrivers, BtnDeleteDrivers })
                if (b != null) b.IsEnabled = !busy;
            StatusText.Text = busy ? (label ?? "正在处理…") : "就绪";
        }

        void BtnBusy_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            Log("已请求停止当前操作", "warn");
            Status("正在停止…");
        }

        bool EnsureAdmin(string operation)
        {
            if (_isAdmin) return true;
            Log($"「{operation}」需要管理员权限，已取消。请点击左下角「以管理员身份重启」。", "err");
            MessageBox.Show(this, $"「{operation}」需要管理员权限。\n\n请先点击左下角「以管理员身份重启」。",
                "需要提权", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        void ApplyAdminUi()
        {
            _isAdmin = Scanner.IsAdmin();
            AdminDot.Fill = _isAdmin ? (Brush)FindResource("Brush.Green") : (Brush)FindResource("Brush.Warn");
            AdminText.Text = _isAdmin ? "已提权：全部功能可用" : "未提权：可扫描，清理需提权";
            BtnElevate.Visibility = _isAdmin ? Visibility.Collapsed : Visibility.Visible;
        }

        void BtnToggleTheme_Click(object sender, RoutedEventArgs e)
        {
            string next = App.CurrentTheme == "dark" ? "light" : "dark";
            App.ApplyTheme(next);
            BtnToggleTheme.Content = next == "light" ? "切换深色主题" : "切换浅色主题";
            ApplyAdminUi();
            Log($"已切换到{(next == "light" ? "浅色" : "深色")}主题（记忆偏好，下次启动生效）", "ok");
            Status(next == "light" ? "浅色主题" : "深色主题");
        }

        void BtnElevate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log("正在以管理员身份重启…", "info");
                Scanner.RelaunchAsAdmin();
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                Log("提权失败：" + ex.Message, "err");
            }
        }

        // ---------------- dashboard ----------------
        async void BtnScanAll_Click(object sender, RoutedEventArgs e)
        {
            _cts = new CancellationTokenSource();
            SetBusy(true, "全面扫描中…");
            BtnScanAll.Content = "扫描中…";
            Log("开始全面扫描（设备 / 服务 / 驱动包）", "info");
            try
            {
                var token = _cts.Token;
                MetricDevices.Text = "…"; MetricServices.Text = "…"; MetricDrivers.Text = "…";

                var devices = await Task.Run(Scanner.ScanAllDevices, token);
                token.ThrowIfCancellationRequested();
                ListDevices.ItemsSource = new ObservableCollection<PhantomDevice>(devices);
                int ghostCount = devices.Count(d => d.IsGhost);
                MetricDevices.Text = ghostCount.ToString();
                MetricDevicesSub.Text = $"全部 {devices.Count} 项";
                DevicesSummary.Text = $"共 {devices.Count} 项：本机所需 {devices.Count - ghostCount}，幽灵设备 {ghostCount}" +
                    (ghostCount > 0 ? "（已默认勾选）" : "");
                DevicesEmpty.Visibility = ghostCount == 0 ? Visibility.Visible : Visibility.Collapsed;
                Log($"设备扫描完成：本机所需 {devices.Count - ghostCount}，幽灵设备 {ghostCount}", "ok");

                var services = await Task.Run(Scanner.ScanSuspectServices, token);
                token.ThrowIfCancellationRequested();
                ListServices.ItemsSource = new ObservableCollection<ServiceInfo>(services);
                MetricServices.Text = services.Count.ToString();
                MetricServicesSub.Text = "已加载到「系统服务」页";
                ServicesSummary.Text = $"共 {services.Count} 个可疑服务候选。";
                Log($"可疑服务：{services.Count} 个", "ok");

                var drivers = await Task.Run(Scanner.ScanUnusedDriverPackages, token);
                token.ThrowIfCancellationRequested();
                ListDrivers.ItemsSource = new ObservableCollection<DriverPackage>(drivers);
                int cleanable = drivers.Count(d => d.Risk == "可清理");
                MetricDrivers.Text = drivers.Count.ToString();
                MetricDriversSub.Text = $"其中 {cleanable} 个可清理";
                DriversSummary.Text = $"共 {drivers.Count} 个未使用包，{cleanable} 个可清理（软件自带已标记为「软件」）。";
                Log($"未使用驱动包：{drivers.Count} 个（可清理 {cleanable}）", "ok");

                Log("全面扫描完成", "ok");
            }
            catch (OperationCanceledException)
            {
                Log("全面扫描已停止", "warn");
            }
            catch (Exception ex)
            {
                Log("扫描失败：" + ex.Message, "err");
            }
            finally
            {
                SetBusy(false);
                BtnScanAll.Content = "开始全面扫描";
            }
        }

        void BtnExportReport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string file = Path.Combine(desktop, $"WinMigrationCleaner_report_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                using var w = new StreamWriter(file);
                w.WriteLine($"WinMigrationCleaner 扫描报告  {DateTime.Now}");
                w.WriteLine(new string('=', 60));
                if (_hwRows != null)
                    foreach (var r in _hwRows)
                        w.WriteLine($"{r.Label}:  {r.Value}");
                w.WriteLine();
                w.WriteLine();
                w.WriteLine($"[设备清单] 共 {(ListDevices.ItemsSource as ObservableCollection<PhantomDevice>)?.Count ?? 0}");
                if (ListDevices.ItemsSource is ObservableCollection<PhantomDevice> devs)
                    foreach (var d in devs) w.WriteLine($"  - [{d.Status}] {d.Name} [{d.Class}]  {d.InstanceId}");
                w.WriteLine();
                w.WriteLine($"[可疑服务] 共 {(ListServices.ItemsSource as ObservableCollection<ServiceInfo>)?.Count ?? 0}");
                if (ListServices.ItemsSource is ObservableCollection<ServiceInfo> svcs)
                    foreach (var s in svcs) w.WriteLine($"  - {s.Name} ({s.DisplayName}) [{s.State}/{s.StartMode}] {s.Reason}");
                w.WriteLine();
                w.WriteLine($"[未使用驱动包] 共 {(ListDrivers.ItemsSource as ObservableCollection<DriverPackage>)?.Count ?? 0}");
                if (ListDrivers.ItemsSource is ObservableCollection<DriverPackage> drv)
                    foreach (var d in drv) w.WriteLine($"  - [{d.Risk}] {d.PublishedName} <- {d.OriginalName} ({d.Provider} {d.Version})");
                Log($"报告已导出：{file}", "ok");
                Status("报告已导出到桌面");
            }
            catch (Exception ex)
            {
                Log("导出失败：" + ex.Message, "err");
            }
        }

        // ---------------- devices ----------------
        void UpdateDeviceMetrics(List<PhantomDevice> devices)
        {
            int ghosts = devices.Count(d => d.IsGhost);
            MetricDevices.Text = ghosts.ToString();
            MetricDevicesSub.Text = $"全部 {devices.Count} 项";
            DevicesSummary.Text = devices.Count == 0
                ? "未发现任何设备记录。"
                : $"共 {devices.Count} 项：本机所需 {devices.Count - ghosts}，幽灵设备 {ghosts}" +
                  (ghosts > 0 ? "（已默认勾选，可直接移除）" : "");
            DevicesEmpty.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        async void BtnScanDevices_Click(object sender, RoutedEventArgs e)
        {
            SetBusy(true, "正在扫描全部设备…");
            Log("扫描设备清单（本机所需 + 幽灵设备）…", "info");
            try
            {
                var devices = await Task.Run(Scanner.ScanAllDevices);
                ListDevices.ItemsSource = new ObservableCollection<PhantomDevice>(devices);
                UpdateDeviceMetrics(devices);
                int ghosts = devices.Count(d => d.IsGhost);
                Log($"设备扫描完成：本机所需 {devices.Count - ghosts}，幽灵设备 {ghosts}", "ok");
            }
            catch (Exception ex) { Log("扫描失败：" + ex.Message, "err"); }
            finally { SetBusy(false); }
        }

        void BtnSelectAllDevices_Click(object sender, RoutedEventArgs e)
        {
            if (ListDevices.ItemsSource is not ObservableCollection<PhantomDevice> list) return;
            var ghosts = list.Where(d => d.IsGhost).ToList();
            bool any = ghosts.Any(d => d.Selected);
            foreach (var d in ghosts) d.Selected = !any;
            DevicesSummary.Text = any ? "已取消全选（仅幽灵设备可勾选）。" : "已全选幽灵设备。";
            Status(any ? "已取消全选" : "已全选幽灵设备");
        }

        async void BtnRemoveDevices_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureAdmin("移除幽灵设备")) return;
            if (ListDevices.ItemsSource is not ObservableCollection<PhantomDevice> list) return;
            var selected = list.Where(d => d.Selected && d.IsGhost).ToList();
            if (selected.Count == 0)
            {
                Log("未选中任何设备", "warn");
                MessageBox.Show(this, "请先勾选要移除的设备。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new ConfirmDialog("移除幽灵设备",
                $"将移除 {selected.Count} 个设备记录。",
                "这些是已断开硬件的残留记录；若以后重新插入该硬件，Windows 会自动重新识别安装。",
                "确认移除");
            dlg.ShowDialog();
            if (!dlg.Confirmed) { Log("已取消移除设备", "info"); return; }

            _cts = new CancellationTokenSource();
            SetBusy(true, "移除设备中…", determinate: true);
            MainProgress.Maximum = selected.Count;
            Log($"开始移除 {selected.Count} 个幽灵设备…", "info");
            int ok = 0, fail = 0, done = 0;
            try
            {
                var token = _cts.Token;
                await Task.Run(() =>
                {
                    foreach (var d in selected)
                    {
                        token.ThrowIfCancellationRequested();
                        Dispatcher.Invoke(() => Status($"移除：{d.Name}"));
                        var (c, _) = Scanner.RunPnPUtil($"/remove-device \"{d.InstanceId}\"", 15000);
                        if (c == 0) Interlocked.Increment(ref ok); else Interlocked.Increment(ref fail);
                        Dispatcher.Invoke(() => { done++; MainProgress.Value = done; });
                    }
                }, token);
                Log($"移除完成：成功 {ok}，失败 {fail}", fail == 0 ? "ok" : "warn");
                DevicesSummary.Text = $"移除成功 {ok} 个，失败 {fail} 个。";
            }
            catch (OperationCanceledException) { Log($"移除已停止（成功 {ok}，失败 {fail}）", "warn"); }
            catch (Exception ex) { Log("移除失败：" + ex.Message, "err"); }
            finally { SetBusy(false); }

            // auto re-scan
            BtnScanDevices_Click(this, e);
        }

        // ---------------- services ----------------
        async void BtnScanServices_Click(object sender, RoutedEventArgs e)
        {
            SetBusy(true, "扫描服务…");
            Log("扫描可疑服务…", "info");
            try
            {
                var services = await Task.Run(Scanner.ScanSuspectServices);
                ListServices.ItemsSource = new ObservableCollection<ServiceInfo>(services);
                MetricServices.Text = services.Count.ToString();
                MetricServicesSub.Text = "已加载";
                ServicesSummary.Text = $"共 {services.Count} 个可疑服务候选。";
                Log($"可疑服务：{services.Count} 个", "ok");
            }
            catch (Exception ex) { Log("扫描失败：" + ex.Message, "err"); }
            finally { SetBusy(false); }
        }

        async void BtnDisableServices_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureAdmin("禁用服务")) return;
            if (ListServices.ItemsSource is not ObservableCollection<ServiceInfo> list) return;
            var selected = list.Where(s => s.Selected).ToList();
            if (selected.Count == 0) { Log("未选中任何服务", "warn"); return; }

            var dlg = new ConfirmDialog("禁用服务",
                $"将把 {selected.Count} 个服务设为「已禁用」。",
                "禁用是可逆操作：之后可随时在 services.msc 中改回自动/手动。", "确认禁用");
            dlg.ShowDialog();
            if (!dlg.Confirmed) { Log("已取消禁用服务", "info"); return; }

            SetBusy(true, "禁用服务中…");
            int ok = 0, fail = 0;
            await Task.Run(() =>
            {
                foreach (var s in selected)
                {
                    Dispatcher.Invoke(() => Status($"禁用：{s.Name}"));
                    if (Scanner.SetServiceStartType(s.Name, 4)) Interlocked.Increment(ref ok);
                    else Interlocked.Increment(ref fail);
                }
            });
            Log($"禁用完成：成功 {ok}，失败 {fail}", fail == 0 ? "ok" : "warn");
            ServicesSummary.Text = $"禁用成功 {ok} 个，失败 {fail} 个。重启后生效。";
            SetBusy(false);
            BtnScanServices_Click(this, e);
        }

        async void BtnDeleteServices_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureAdmin("删除服务")) return;
            if (ListServices.ItemsSource is not ObservableCollection<ServiceInfo> list) return;
            var selected = list.Where(s => s.Selected).ToList();
            if (selected.Count == 0) { Log("未选中任何服务", "warn"); return; }

            var dlg = new ConfirmDialog("删除服务",
                $"将永久删除 {selected.Count} 个服务。",
                "删除不可逆（重装对应驱动/软件可恢复）。确定这些服务属于已不存在的旧硬件吗？", "永久删除");
            dlg.ShowDialog();
            if (!dlg.Confirmed) { Log("已取消删除服务", "info"); return; }

            SetBusy(true, "删除服务中…");
            int ok = 0, fail = 0;
            await Task.Run(() =>
            {
                foreach (var s in selected)
                {
                    Dispatcher.Invoke(() => Status($"删除服务：{s.Name}"));
                    if (Scanner.DeleteService(s.Name)) Interlocked.Increment(ref ok);
                    else Interlocked.Increment(ref fail);
                }
            });
            Log($"服务删除完成：成功 {ok}，失败 {fail}", fail == 0 ? "ok" : "warn");
            ServicesSummary.Text = $"删除成功 {ok} 个，失败 {fail} 个。";
            SetBusy(false);
            BtnScanServices_Click(this, e);
        }

        // ---------------- drivers ----------------
        async void BtnScanDrivers_Click(object sender, RoutedEventArgs e)
        {
            SetBusy(true, "扫描驱动包…");
            Log("扫描未使用驱动包…", "info");
            try
            {
                var drivers = await Task.Run(Scanner.ScanUnusedDriverPackages);
                ListDrivers.ItemsSource = new ObservableCollection<DriverPackage>(drivers);
                int cleanable = drivers.Count(d => d.Risk == "可清理");
                MetricDrivers.Text = drivers.Count.ToString();
                MetricDriversSub.Text = $"其中 {cleanable} 个可清理";
                DriversSummary.Text = $"共 {drivers.Count} 个未使用包，{cleanable} 个可清理。";
                Log($"未使用驱动包：{drivers.Count} 个（可清理 {cleanable}）", "ok");
            }
            catch (Exception ex) { Log("扫描失败：" + ex.Message, "err"); }
            finally { SetBusy(false); }
        }

        async void BtnDeleteDrivers_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureAdmin("删除驱动包")) return;
            if (ListDrivers.ItemsSource is not ObservableCollection<DriverPackage> list) return;
            var selected = list.Where(d => d.Selected && d.Risk == "可清理").ToList();
            if (selected.Count == 0)
            {
                Log("未选中任何「可清理」驱动包（「软件」与「保留」项不可删除）", "warn");
                MessageBox.Show(this, "只能删除标记为「可清理」的驱动包。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new ConfirmDialog("删除驱动包",
                $"将从 DriverStore 永久删除 {selected.Count} 个驱动包。",
                string.Join("\n", selected.Take(8).Select(d => $"• {d.PublishedName} ← {d.OriginalName}")) +
                (selected.Count > 8 ? $"\n• … 共 {selected.Count} 个" : "") +
                "\n\n此操作不可逆。删除「软件」类驱动包可能破坏已安装软件，因此本工具已将其排除。",
                "永久删除");
            dlg.ShowDialog();
            if (!dlg.Confirmed) { Log("已取消删除驱动包", "info"); return; }

            _cts = new CancellationTokenSource();
            SetBusy(true, "删除驱动包中…");
            int ok = 0, fail = 0;
            try
            {
                var token = _cts.Token;
                await Task.Run(() =>
                {
                    foreach (var d in selected)
                    {
                        token.ThrowIfCancellationRequested();
                        Dispatcher.Invoke(() => Status($"删除：{d.PublishedName}"));
                        var (c, _) = Scanner.RunPnPUtil($"/delete-driver {d.PublishedName} /uninstall /force", 30000);
                        if (c == 0) Interlocked.Increment(ref ok); else Interlocked.Increment(ref fail);
                    }
                }, token);
                Log($"驱动包删除完成：成功 {ok}，失败 {fail}", fail == 0 ? "ok" : "warn");
                DriversSummary.Text = $"删除成功 {ok} 个，失败 {fail} 个。";
            }
            catch (OperationCanceledException) { Log($"删除已停止（成功 {ok}，失败 {fail}）", "warn"); }
            catch (Exception ex) { Log("删除失败：" + ex.Message, "err"); }
            finally { SetBusy(false); }

            BtnScanDrivers_Click(this, e);
        }

        void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            _logs.Clear();
            Status("日志已清空");
        }
    }
}
