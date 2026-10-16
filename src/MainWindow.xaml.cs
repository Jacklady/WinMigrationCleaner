using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinMigrationCleaner
{
    public partial class MainWindow : Window
    {
        readonly ObservableCollection<LogEntry> _logs = new();
        CancellationTokenSource? _cts;
        bool _isAdmin;
        bool _busy;
        bool _scanned;
        Task _scanTask = Task.CompletedTask;
        string _activePage = "dash";

        // master data (unfiltered); views are filtered/sorted copies
        List<PhantomDevice> _devices = new();
        List<ServiceInfo> _services = new();
        List<DriverPackage> _drivers = new();
        List<HardwareRow>? _hwRows;

        // sort state (null = default: ghosts/cleanable first)
        string? _devSort; bool _devAsc;
        string? _svcSort; bool _svcAsc = true;
        string? _drvSort; bool _drvAsc = true;

        public MainWindow()
        {
            InitializeComponent();
            LogList.ItemsSource = _logs;
            Loaded += MainWindow_Loaded;
        }

        // ---------------- init ----------------
        void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyAdminUi();
            BtnToggleTheme.Content = App.CurrentTheme == "light" ? "切换深色主题" : "切换浅色主题";

            Log("欢迎使用 WinMigrationCleaner v1.1", "ok");
            Log(_isAdmin ? "已以管理员身份运行" : "提示：清理操作需要管理员权限（左下角可一键提权重启）", "info");

            // hardware info (fast, sync ok)
            _hwRows = Scanner.GetHardwareInfo();
            InfoList.ItemsSource = _hwRows;

            // v1.1: scan automatically on startup — no clicks needed
            RunFullScan();

            // diagnostic self-screenshot mode
            if (App.AutoScreenshot != null)
            {
                var path = App.AutoScreenshot;
                var page = App.AutoScreenshotPage;
                Dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        await _scanTask;
                        if (page == "devices") NavTo(NavDevices); else NavTo(NavDash);
                        await Task.Delay(600);
                        var rtb = new RenderTargetBitmap(
                            (int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
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

        // ---------------- navigation ----------------
        void Nav_Click(object sender, RoutedEventArgs e)
        {
            NavTo((Button)sender);
        }

        void NavTo(Button btn)
        {
            foreach (var b in new[] { NavDash, NavDevices, NavServices, NavDrivers, NavAbout })
                b.Tag = b == btn ? "active" : null;

            PageDash.Visibility = btn == NavDash ? Visibility.Visible : Visibility.Collapsed;
            PageDevices.Visibility = btn == NavDevices ? Visibility.Visible : Visibility.Collapsed;
            PageServices.Visibility = btn == NavServices ? Visibility.Visible : Visibility.Collapsed;
            PageDrivers.Visibility = btn == NavDrivers ? Visibility.Visible : Visibility.Collapsed;
            PageAbout.Visibility = btn == NavAbout ? Visibility.Visible : Visibility.Collapsed;
            _activePage = btn == NavDash ? "dash" : btn == NavDevices ? "devices"
                        : btn == NavServices ? "services" : btn == NavDrivers ? "drivers" : "about";
            string name = _activePage switch
            {
                "dash" => "仪表盘", "devices" => "幽灵设备", "services" => "系统服务",
                "drivers" => "驱动包", _ => "关于"
            };
            Status($"已切换到「{name}」");
        }

        void MetricCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border b && b.Tag is string page)
            {
                Button target = page switch
                {
                    "devices" => NavDevices,
                    "services" => NavServices,
                    "drivers" => NavDrivers,
                    _ => NavDash
                };
                NavTo(target);
            }
        }

        // ---------------- keyboard shortcuts ----------------
        void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5)
            {
                if (!_busy) BtnScanAll_Click(this, e);
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
            {
                TextBox? search = _activePage switch
                {
                    "devices" => SearchDevices,
                    "services" => SearchServices,
                    "drivers" => SearchDrivers,
                    _ => null
                };
                if (search != null)
                {
                    if (_activePage == "devices") NavTo(NavDevices);
                    else if (_activePage == "services") NavTo(NavServices);
                    else if (_activePage == "drivers") NavTo(NavDrivers);
                    search.Focus();
                    search.SelectAll();
                    e.Handled = true;
                }
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.A)
            {
                if (Keyboard.FocusedElement is TextBox tb) { tb.SelectAll(); e.Handled = true; return; }
                if (_activePage == "devices") BtnSelectAllDevices_Click(this, e);
                else if (_activePage == "services") BtnSelectAllServices_Click(this, e);
                else if (_activePage == "drivers") BtnSelectAllDrivers_Click(this, e);
                e.Handled = true;
            }
        }

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
            _busy = busy;
            MainProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            MainProgress.IsIndeterminate = !determinate;
            if (determinate) MainProgress.Value = 0;
            BtnBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            foreach (var b in new Button?[] { BtnScanAll, BtnExportReport, BtnQuickClean,
                     BtnScanDevices, BtnRemoveDevices, BtnSelectAllDevices,
                     BtnScanServices, BtnDeleteServices, BtnDisableServices, BtnSelectAllServices,
                     BtnScanDrivers, BtnDeleteDrivers, BtnSelectAllDrivers })
                if (b != null) b.IsEnabled = !busy;
            if (!busy) { UpdateDeviceSelectionUi(); UpdateServiceSelectionUi(); UpdateDriverSelectionUi(); }
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

        // ---------------- full scan (dashboard) ----------------
        void RunFullScan()
        {
            if (_busy) return;
            _scanTask = RunFullScanCoreAsync();
        }

        async void BtnScanAll_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            await RunFullScanCoreAsync();
        }

        async Task RunFullScanCoreAsync()
        {
            _cts = new CancellationTokenSource();
            SetBusy(true, "全面扫描中…");
            BtnScanAll.Content = "扫描中…";
            Log("开始全面扫描（设备 / 服务 / 驱动包）", "info");
            try
            {
                var token = _cts.Token;
                MetricDevices.Text = "…"; MetricServices.Text = "…"; MetricDrivers.Text = "…";

                _devices = await Task.Run(Scanner.ScanAllDevices, token);
                token.ThrowIfCancellationRequested();
                int ghostCount = _devices.Count(d => d.IsGhost);
                // v1.1: ghost devices are pre-selected so "移除所选" is one click away
                foreach (var d in _devices.Where(d => d.IsGhost)) d.Selected = true;
                Log($"设备扫描完成：本机所需 {_devices.Count - ghostCount}，幽灵设备 {ghostCount}", "ok");

                _services = await Task.Run(Scanner.ScanSuspectServices, token);
                token.ThrowIfCancellationRequested();
                Log($"可疑服务：{_services.Count} 个", "ok");

                _drivers = await Task.Run(Scanner.ScanUnusedDriverPackages, token);
                token.ThrowIfCancellationRequested();
                int cleanable = _drivers.Count(d => d.Risk == "可清理");
                Log($"未使用驱动包：{_drivers.Count} 个（可清理 {cleanable}）", "ok");

                _scanned = true;
                RefreshDeviceView();
                RefreshServiceView();
                RefreshDriverView();
                UpdateMetrics();
                UpdateQuickCleanCard();
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
                BtnScanAll.Content = "重新扫描 (F5)";
            }
        }

        // ---------------- metrics / badges / quick clean ----------------
        void UpdateMetrics()
        {
            int ghosts = _devices.Count(d => d.IsGhost);
            int cleanable = _drivers.Count(d => d.Risk == "可清理");

            MetricDevices.Text = ghosts.ToString();
            MetricDevicesSub.Text = _scanned ? $"本机所需 {_devices.Count - ghosts} 项（已保护）" : "未扫描";
            MetricServices.Text = _services.Count.ToString();
            MetricServicesSub.Text = _scanned ? "命中厂商关键字的服务候选" : "未扫描";
            MetricDrivers.Text = _drivers.Count.ToString();
            MetricDriversSub.Text = _scanned ? $"其中 {cleanable} 个可安全删除" : "未扫描";

            // sidebar badges
            SetBadge(BadgeDevices, BadgeDevicesText, ghosts);
            SetBadge(BadgeServices, BadgeServicesText, _services.Count);
            SetBadge(BadgeDrivers, BadgeDriversText, cleanable);

            // summaries
            DevicesSummary.Text = _devices.Count == 0
                ? "未发现任何设备记录。"
                : $"共 {_devices.Count} 项：本机所需 {_devices.Count - ghosts}，幽灵设备 {ghosts}（已默认勾选）";
            ServicesSummary.Text = $"共 {_services.Count} 个可疑服务候选。建议先禁用观察，确认后再删除。";
            DriversSummary.Text = $"共 {_drivers.Count} 个未使用包，{cleanable} 个可清理（软件自带已标记为「软件」）。";

            DevicesEmpty.Visibility = _devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_devices.Count == 0) DevicesEmptyText.Text = "没有发现幽灵设备 —— 当前硬件的记录都在正常工作";
            ServicesEmpty.Visibility = _services.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            DriversEmpty.Visibility = _drivers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        static void SetBadge(Border badge, TextBlock text, int count)
        {
            if (count <= 0) { badge.Visibility = Visibility.Collapsed; return; }
            text.Text = count > 999 ? "999+" : count.ToString();
            badge.Visibility = Visibility.Visible;
        }

        void UpdateQuickCleanCard()
        {
            int ghosts = _devices.Count(d => d.IsGhost);
            int cleanable = _drivers.Count(d => d.Risk == "可清理");
            int total = ghosts + cleanable;
            if (!_scanned || total == 0)
            {
                QuickCleanCard.Visibility = Visibility.Collapsed;
                return;
            }
            QuickCleanCard.Visibility = Visibility.Visible;
            QuickCleanText.Text = $"检测到 {ghosts} 个幽灵设备和 {cleanable} 个可清理驱动包，全部属于安全项。" +
                "一键移除（操作前会二次确认）。若还有可疑服务，请到「系统服务」页单独确认处理。";
            BtnQuickClean.Content = $"一键清理（{total}）";
        }

        // ---------------- quick clean (one click) ----------------
        async void BtnQuickClean_Click(object sender, RoutedEventArgs e)
        {
            if (!_scanned) return;
            var ghosts = _devices.Where(d => d.IsGhost).ToList();
            var drv = _drivers.Where(d => d.Risk == "可清理").ToList();
            if (ghosts.Count == 0 && drv.Count == 0)
            {
                Log("没有可清理的安全项", "info");
                return;
            }
            if (!EnsureAdmin("一键清理")) return;

            var dlg = new ConfirmDialog("一键清理安全项",
                $"将移除 {ghosts.Count} 个幽灵设备记录，并从 DriverStore 删除 {drv.Count} 个可清理驱动包。",
                "• 幽灵设备是已断开硬件的残留记录，重新插入硬件会自动重新识别\n" +
                "• 驱动包均为「可清理」分级：无设备引用、非当前平台组件、非已安装软件\n" +
                "• 此操作不可逆，建议清理后重启一次让变更完全生效",
                "确认清理");
            dlg.ShowDialog();
            if (!dlg.Confirmed) { Log("已取消一键清理", "info"); return; }

            _cts = new CancellationTokenSource();
            SetBusy(true, "一键清理中…");
            int devOk = 0, devFail = 0, drvOk = 0, drvFail = 0;
            try
            {
                if (ghosts.Count > 0)
                {
                    Log($"开始移除 {ghosts.Count} 个幽灵设备…", "info");
                    (devOk, devFail) = await RemoveDevicesCoreAsync(ghosts, _cts.Token);
                }
                if (drv.Count > 0 && !_cts.Token.IsCancellationRequested)
                {
                    Log($"开始删除 {drv.Count} 个可清理驱动包…", "info");
                    (drvOk, drvFail) = await DeleteDriversCoreAsync(drv, _cts.Token);
                }
                Log($"一键清理完成：设备 成功 {devOk}/失败 {devFail}，驱动包 成功 {drvOk}/失败 {drvFail}",
                    devFail + drvFail == 0 ? "ok" : "warn");
            }
            catch (OperationCanceledException)
            {
                Log($"已停止（设备 成功 {devOk}/失败 {devFail}，驱动包 成功 {drvOk}/失败 {drvFail}）", "warn");
            }
            catch (Exception ex)
            {
                Log("清理失败：" + ex.Message, "err");
            }
            finally
            {
                SetBusy(false);
            }

            await RescanAllQuietAsync();
        }

        async Task RescanAllQuietAsync()
        {
            try
            {
                _devices = await Task.Run(Scanner.ScanAllDevices);
                _services = await Task.Run(Scanner.ScanSuspectServices);
                _drivers = await Task.Run(Scanner.ScanUnusedDriverPackages);
                _scanned = true;
                RefreshDeviceView(); RefreshServiceView(); RefreshDriverView();
                UpdateMetrics(); UpdateQuickCleanCard();
            }
            catch (Exception ex) { Log("重新扫描失败：" + ex.Message, "err"); }
        }

        // ---------------- export report ----------------
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
                w.WriteLine("\r\n[设备清单]");
                foreach (var d in _devices) w.WriteLine($"  - [{d.Status}] {d.Name} [{d.Class}]  {d.InstanceId}");
                w.WriteLine("\r\n[可疑服务]");
                foreach (var s in _services) w.WriteLine($"  - {s.Name} ({s.DisplayName}) [{s.State}/{s.StartMode}] {s.Reason}");
                w.WriteLine("\r\n[未使用驱动包]");
                foreach (var d in _drivers) w.WriteLine($"  - [{d.Risk}] {d.PublishedName} <- {d.OriginalName} ({d.Provider} {d.Version})");
                Log($"报告已导出：{file}", "ok");
                Status("报告已导出到桌面");
            }
            catch (Exception ex)
            {
                Log("导出失败：" + ex.Message, "err");
            }
        }

        // ---------------- devices page ----------------
        async void BtnScanDevices_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            SetBusy(true, "正在扫描全部设备…");
            Log("扫描设备清单（本机所需 + 幽灵设备）…", "info");
            try
            {
                _devices = await Task.Run(Scanner.ScanAllDevices);
                foreach (var d in _devices.Where(d => d.IsGhost)) d.Selected = true;
                _scanned = true;
                RefreshDeviceView(); UpdateMetrics(); UpdateQuickCleanCard();
                int ghosts = _devices.Count(d => d.IsGhost);
                Log($"设备扫描完成：本机所需 {_devices.Count - ghosts}，幽灵设备 {ghosts}", "ok");
            }
            catch (Exception ex) { Log("扫描失败：" + ex.Message, "err"); }
            finally { SetBusy(false); }
        }

        void BtnSelectAllDevices_Click(object sender, RoutedEventArgs e)
        {
            if (_devices.Count == 0) return;
            var ghosts = _devices.Where(d => d.IsGhost).ToList();
            bool any = ghosts.Any(d => d.Selected);
            foreach (var d in ghosts) d.Selected = !any;
            Status(any ? "已取消全选" : "已全选幽灵设备");
            UpdateDeviceSelectionUi();
        }

        void DevCheck_Click(object sender, RoutedEventArgs e) => UpdateDeviceSelectionUi();

        void UpdateDeviceSelectionUi()
        {
            int n = _devices.Count(d => d.Selected && d.IsGhost);
            BtnRemoveDevices.Content = n > 0 ? $"移除所选（{n}）" : "移除所选";
            BtnRemoveDevices.IsEnabled = n > 0 && !_busy;
        }

        async void BtnRemoveDevices_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureAdmin("移除幽灵设备")) return;
            var selected = _devices.Where(d => d.Selected && d.IsGhost).ToList();
            if (selected.Count == 0)
            {
                Log("未选中任何设备", "warn");
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
            int ok = 0, fail = 0;
            try
            {
                (ok, fail) = await RemoveDevicesCoreAsync(selected, _cts.Token);
                Log($"移除完成：成功 {ok}，失败 {fail}", fail == 0 ? "ok" : "warn");
            }
            catch (OperationCanceledException) { Log($"移除已停止（成功 {ok}，失败 {fail}）", "warn"); }
            catch (Exception ex) { Log("移除失败：" + ex.Message, "err"); }
            finally { SetBusy(false); }

            await RescanAllQuietAsync();
        }

        async Task<(int ok, int fail)> RemoveDevicesCoreAsync(List<PhantomDevice> selected, CancellationToken token)
        {
            int ok = 0, fail = 0, done = 0;
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
            return (ok, fail);
        }

        // ---------------- devices view (filter + sort) ----------------
        void SearchDevices_TextChanged(object sender, TextChangedEventArgs e) => RefreshDeviceView();

        void DeviceHeader_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not GridViewColumnHeader h || h.Tag is not string prop) return;
            if (_devSort == prop) _devAsc = !_devAsc;
            else { _devSort = prop; _devAsc = true; }
            MarkSorted(ListDevices, _devSort, _devAsc);
            RefreshDeviceView();
        }

        void RefreshDeviceView()
        {
            IEnumerable<PhantomDevice> src = _devices;
            var q = SearchDevices.Text.Trim();
            if (q.Length > 0)
                src = src.Where(d => d.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                  || d.Class.Contains(q, StringComparison.OrdinalIgnoreCase)
                                  || d.InstanceId.Contains(q, StringComparison.OrdinalIgnoreCase));
            src = _devSort switch
            {
                "Name" => _devAsc ? src.OrderBy(d => d.Name) : src.OrderByDescending(d => d.Name),
                "Class" => _devAsc ? src.OrderBy(d => d.Class) : src.OrderByDescending(d => d.Class),
                "InstanceId" => _devAsc ? src.OrderBy(d => d.InstanceId) : src.OrderByDescending(d => d.InstanceId),
                "IsGhost" => _devAsc ? src.OrderBy(d => d.IsGhost).ThenBy(d => d.Name)
                                     : src.OrderByDescending(d => d.IsGhost).ThenBy(d => d.Name),
                _ => src.OrderByDescending(d => d.IsGhost).ThenBy(d => d.Name)
            };
            var view = new ObservableCollection<PhantomDevice>(src);
            ListDevices.ItemsSource = view;

            if (_devices.Count > 0 && view.Count == 0)
            {
                DevicesEmpty.Visibility = Visibility.Visible;
                DevicesEmptyText.Text = "没有匹配搜索条件的设备";
            }
            else if (_devices.Count == 0)
            {
                DevicesEmpty.Visibility = Visibility.Visible;
                DevicesEmptyText.Text = "没有发现幽灵设备 —— 当前硬件的记录都在正常工作";
            }
            else DevicesEmpty.Visibility = Visibility.Collapsed;

            int ghosts = _devices.Count(d => d.IsGhost);
            int sel = _devices.Count(d => d.Selected && d.IsGhost);
            DevicesSummary.Text = _devices.Count == 0 ? "尚未发现设备记录。"
                : $"共 {_devices.Count} 项：本机所需 {_devices.Count - ghosts}，幽灵设备 {ghosts}（已选 {sel}）"
                  + (q.Length > 0 ? $" · 筛选显示 {view.Count} 项" : "");
            UpdateDeviceSelectionUi();
        }

        // ---------------- services page ----------------
        async void BtnScanServices_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            SetBusy(true, "扫描服务…");
            Log("扫描可疑服务…", "info");
            try
            {
                _services = await Task.Run(Scanner.ScanSuspectServices);
                _scanned = true;
                RefreshServiceView(); UpdateMetrics(); UpdateQuickCleanCard();
                Log($"可疑服务：{_services.Count} 个", "ok");
            }
            catch (Exception ex) { Log("扫描失败：" + ex.Message, "err"); }
            finally { SetBusy(false); }
        }

        void BtnSelectAllServices_Click(object sender, RoutedEventArgs e)
        {
            if (_services.Count == 0) return;
            bool any = _services.Any(s => s.Selected);
            foreach (var s in _services) s.Selected = !any;
            Status(any ? "已取消全选" : "已全选服务候选");
            UpdateServiceSelectionUi();
        }

        void SvcCheck_Click(object sender, RoutedEventArgs e) => UpdateServiceSelectionUi();

        void UpdateServiceSelectionUi()
        {
            int n = _services.Count(s => s.Selected);
            BtnDisableServices.Content = n > 0 ? $"禁用所选（{n}）" : "禁用所选";
            BtnDeleteServices.Content = n > 0 ? $"删除所选（{n}）" : "删除所选";
            BtnDisableServices.IsEnabled = n > 0 && !_busy;
            BtnDeleteServices.IsEnabled = n > 0 && !_busy;
        }

        async void BtnDisableServices_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureAdmin("禁用服务")) return;
            var selected = _services.Where(s => s.Selected).ToList();
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
            SetBusy(false);
            await RescanAllQuietAsync();
        }

        async void BtnDeleteServices_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureAdmin("删除服务")) return;
            var selected = _services.Where(s => s.Selected).ToList();
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
            SetBusy(false);
            await RescanAllQuietAsync();
        }

        void SearchServices_TextChanged(object sender, TextChangedEventArgs e) => RefreshServiceView();

        void ServiceHeader_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not GridViewColumnHeader h || h.Tag is not string prop) return;
            if (_svcSort == prop) _svcAsc = !_svcAsc;
            else { _svcSort = prop; _svcAsc = true; }
            MarkSorted(ListServices, _svcSort, _svcAsc);
            RefreshServiceView();
        }

        void RefreshServiceView()
        {
            IEnumerable<ServiceInfo> src = _services;
            var q = SearchServices.Text.Trim();
            if (q.Length > 0)
                src = src.Where(s => s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                  || s.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                                  || s.Reason.Contains(q, StringComparison.OrdinalIgnoreCase));
            src = _svcSort switch
            {
                "Name" => _svcAsc ? src.OrderBy(s => s.Name) : src.OrderByDescending(s => s.Name),
                "DisplayName" => _svcAsc ? src.OrderBy(s => s.DisplayName) : src.OrderByDescending(s => s.DisplayName),
                "State" => _svcAsc ? src.OrderBy(s => s.State) : src.OrderByDescending(s => s.State),
                "StartMode" => _svcAsc ? src.OrderBy(s => s.StartMode) : src.OrderByDescending(s => s.StartMode),
                "Reason" => _svcAsc ? src.OrderBy(s => s.Reason) : src.OrderByDescending(s => s.Reason),
                _ => src.OrderBy(s => s.Name)
            };
            var view = new ObservableCollection<ServiceInfo>(src);
            ListServices.ItemsSource = view;

            if (_services.Count > 0 && view.Count == 0)
            {
                ServicesEmpty.Visibility = Visibility.Visible;
                ServicesEmptyText.Text = "没有匹配搜索条件的服务";
            }
            else ServicesEmpty.Visibility = _services.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            int sel = _services.Count(s => s.Selected);
            ServicesSummary.Text = _services.Count == 0 ? "未发现候选服务。"
                : $"共 {_services.Count} 个可疑服务候选（已选 {sel}）" + (q.Length > 0 ? $" · 筛选显示 {view.Count} 项" : "");
            UpdateServiceSelectionUi();
        }

        // ---------------- drivers page ----------------
        async void BtnScanDrivers_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            SetBusy(true, "扫描驱动包…");
            Log("扫描未使用驱动包…", "info");
            try
            {
                _drivers = await Task.Run(Scanner.ScanUnusedDriverPackages);
                _scanned = true;
                RefreshDriverView(); UpdateMetrics(); UpdateQuickCleanCard();
                int cleanable = _drivers.Count(d => d.Risk == "可清理");
                Log($"未使用驱动包：{_drivers.Count} 个（可清理 {cleanable}）", "ok");
            }
            catch (Exception ex) { Log("扫描失败：" + ex.Message, "err"); }
            finally { SetBusy(false); }
        }

        void BtnSelectAllDrivers_Click(object sender, RoutedEventArgs e)
        {
            if (_drivers.Count == 0) return;
            var cleanable = _drivers.Where(d => d.CanSelect).ToList();
            bool any = cleanable.Any(d => d.Selected);
            foreach (var d in cleanable) d.Selected = !any;
            Status(any ? "已取消全选" : "已全选可清理驱动包");
            UpdateDriverSelectionUi();
        }

        void DrvCheck_Click(object sender, RoutedEventArgs e) => UpdateDriverSelectionUi();

        void UpdateDriverSelectionUi()
        {
            int n = _drivers.Count(d => d.Selected && d.CanSelect);
            BtnDeleteDrivers.Content = n > 0 ? $"删除所选「可清理」（{n}）" : "删除所选「可清理」";
            BtnDeleteDrivers.IsEnabled = n > 0 && !_busy;
        }

        async void BtnDeleteDrivers_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureAdmin("删除驱动包")) return;
            var selected = _drivers.Where(d => d.Selected && d.CanSelect).ToList();
            if (selected.Count == 0)
            {
                Log("未选中任何「可清理」驱动包（「软件」与「保留」项不可删除）", "warn");
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
                (ok, fail) = await DeleteDriversCoreAsync(selected, _cts.Token);
                Log($"驱动包删除完成：成功 {ok}，失败 {fail}", fail == 0 ? "ok" : "warn");
            }
            catch (OperationCanceledException) { Log($"删除已停止（成功 {ok}，失败 {fail}）", "warn"); }
            catch (Exception ex) { Log("删除失败：" + ex.Message, "err"); }
            finally { SetBusy(false); }

            await RescanAllQuietAsync();
        }

        async Task<(int ok, int fail)> DeleteDriversCoreAsync(List<DriverPackage> selected, CancellationToken token)
        {
            int ok = 0, fail = 0;
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
            return (ok, fail);
        }

        void SearchDrivers_TextChanged(object sender, TextChangedEventArgs e) => RefreshDriverView();

        void DriverHeader_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not GridViewColumnHeader h || h.Tag is not string prop) return;
            if (_drvSort == prop) _drvAsc = !_drvAsc;
            else { _drvSort = prop; _drvAsc = true; }
            MarkSorted(ListDrivers, _drvSort, _drvAsc);
            RefreshDriverView();
        }

        void RefreshDriverView()
        {
            IEnumerable<DriverPackage> src = _drivers;
            var q = SearchDrivers.Text.Trim();
            if (q.Length > 0)
                src = src.Where(d => d.PublishedName.Contains(q, StringComparison.OrdinalIgnoreCase)
                                  || d.OriginalName.Contains(q, StringComparison.OrdinalIgnoreCase)
                                  || d.Provider.Contains(q, StringComparison.OrdinalIgnoreCase)
                                  || d.Risk.Contains(q, StringComparison.OrdinalIgnoreCase));
            src = _drvSort switch
            {
                "PublishedName" => _drvAsc ? src.OrderBy(d => d.PublishedName) : src.OrderByDescending(d => d.PublishedName),
                "OriginalName" => _drvAsc ? src.OrderBy(d => d.OriginalName) : src.OrderByDescending(d => d.OriginalName),
                "Provider" => _drvAsc ? src.OrderBy(d => d.Provider) : src.OrderByDescending(d => d.Provider),
                "Version" => _drvAsc ? src.OrderBy(d => d.Version) : src.OrderByDescending(d => d.Version),
                "Risk" => _drvAsc ? src.OrderBy(d => d.Risk).ThenBy(d => d.OriginalName)
                                  : src.OrderByDescending(d => d.Risk).ThenBy(d => d.OriginalName),
                _ => src.OrderBy(d => d.Risk == "可清理" ? 0 : d.Risk == "软件" ? 1 : 2).ThenBy(d => d.OriginalName)
            };
            var view = new ObservableCollection<DriverPackage>(src);
            ListDrivers.ItemsSource = view;

            if (_drivers.Count > 0 && view.Count == 0)
            {
                DriversEmpty.Visibility = Visibility.Visible;
                DriversEmptyText.Text = "没有匹配搜索条件的驱动包";
            }
            else DriversEmpty.Visibility = _drivers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            int cleanable = _drivers.Count(d => d.Risk == "可清理");
            int sel = _drivers.Count(d => d.Selected && d.CanSelect);
            DriversSummary.Text = _drivers.Count == 0 ? "未发现未使用驱动包。"
                : $"共 {_drivers.Count} 个未使用包，{cleanable} 个可清理（已选 {sel}）" + (q.Length > 0 ? $" · 筛选显示 {view.Count} 项" : "");
            UpdateDriverSelectionUi();
        }

        // ---------------- sort arrow helper ----------------
        static void MarkSorted(ListView lv, string? prop, bool asc)
        {
            if (lv.View is not GridView gv) return;
            foreach (var col in gv.Columns)
            {
                if (col.Header is not GridViewColumnHeader h) continue;
                if (h.Template?.FindName("arrow", h) is TextBlock arrow)
                {
                    bool match = prop != null && (h.Tag as string) == prop;
                    arrow.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                    if (match) arrow.Text = asc ? " ↑" : " ↓";
                }
            }
        }

        // ---------------- activity log ----------------
        void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            _logs.Clear();
            Status("日志已清空");
        }

        void BtnToggleLog_Click(object sender, RoutedEventArgs e)
        {
            bool collapse = LogList.Visibility == Visibility.Visible;
            LogList.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
            BtnToggleLog.Content = collapse ? "展开 ▸" : "收起 ▾";
        }
    }
}
