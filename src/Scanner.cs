using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;
using System.Management;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WinMigrationCleaner
{
    /// <summary>All scanning / mutation logic. Every method is UI-agnostic.</summary>
    public static class Scanner
    {
        // Vendor keywords that identify leftover services/driver packages from
        // hardware vendors of a previous machine. Extend as needed.
        public static readonly string[] VendorKeywords =
        {
            "Lenovo", "Tobii", "AMD", "Radeon", "Asus", "Acer", "MSI", "Dell", "HP ",
            "Samsung", "RstMw", "iaStor", "Optane", "Rapid Storage", "Crash Defender",
            "mtk", "MediaTek"
        };

        // Software whose driver packages are intentionally "unused" but must NOT
        // be flagged for deletion.
        public static readonly string[] ProtectedSoftware =
        {
            "npcap", "vmware", "vmusb", "sangfor", "gameviewer", "mumu", "pico",
            "tdidd", "oray", "urdp", "ftdi", "apple", "itunes", "prn", "ntprint",
            "logi", "realtek", "vol", "nahimic", "csaudio", "netbridge", "winusb",
            "stmcdc", "iosn", "ibtusb", "netwtw", "iigd", "ipf", "dtt", "igs",
            "hwusb", "fusbhub", "rt-usb", "rt6", "rt2", "rdrv", "vmm", "acpivpc"
        };

        public static readonly string[] VirtualGpuKeywords =
        {
            "virtual", "idd", "oray", "mumu", "todesk", "gameviewer", "pico",
            "sangfor", "parsec", "splashtop", "teamviewer", "sunlogin"
        };

        public static bool IsVirtualGpu(string name) =>
            VirtualGpuKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));

        // Driver packages of the CURRENT platform's core devices. Never suggest removal.
        public static readonly string[] KeepPackages =
        {
            "nvmdi", "nvmd.inf", "nvhda", "nvvad", "nvppc", "nvpcf", "nvlti", // NVIDIA suite
            "ibtusb", "ibtpci", "netwtw", "netwtw6e", "iigd_dch", "ipf_", "dtt_"
        };

        public static bool IsAdmin()
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }

        public static void RelaunchAsAdmin()
        {
            var exe = Environment.ProcessPath!;
            Process.Start(new ProcessStartInfo(exe) { Verb = "runas", UseShellExecute = true });
        }

        [DllImport("user32.dll")]
        private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public uint dmFields;
            public int dmPositionX, dmPositionY;
            public uint dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmNup, dmDisplayFlags, dmDisplayFrequency;
            public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        // ---------------- Hardware info ----------------
        public static List<HardwareRow> GetHardwareInfo()
        {
            var rows = new List<HardwareRow>();
            static object? GetProp(ManagementBaseObject o, string name)
            {
                try { return o[name]; } catch { return null; }
            }
            void Diag(string where, Exception ex)
            {
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "wmc_hw_diag.txt"),
                    $"{DateTime.Now:HH:mm:ss} [{where}] {ex}\r\n"); } catch { }
            }

            // 操作系统
            try
            {
                using var os = new ManagementObjectSearcher("SELECT Caption, BuildNumber FROM Win32_OperatingSystem").Get();
                foreach (var o in os)
                {
                    string cap = GetProp(o, "Caption")?.ToString() ?? "";
                    string build = GetProp(o, "BuildNumber")?.ToString() ?? "";
                    string disp = "";
                    using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                    disp = k?.GetValue("DisplayVersion")?.ToString() ?? "";
                    rows.Add(new HardwareRow { Label = "系统", Value = $"{cap} · 版本 {disp} · 内部版本 {build}" });
                    break;
                }
            }
            catch { }

            // 处理器
            try
            {
                using var cpu = new ManagementObjectSearcher(
                    "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, CurrentClockSpeed FROM Win32_Processor").Get();
                foreach (var o in cpu)
                {
                    string name = (GetProp(o, "Name")?.ToString() ?? "").Trim();
                    int cores = Convert.ToInt32(GetProp(o, "NumberOfCores") ?? 0);
                    int threads = Convert.ToInt32(GetProp(o, "NumberOfLogicalProcessors") ?? 0);
                    int cur = Convert.ToInt32(GetProp(o, "CurrentClockSpeed") ?? 0);
                    rows.Add(new HardwareRow { Label = "处理器", Value = $"{name} · {cores} 核 {threads} 线程 · 当前频率 {cur} MHz" });
                    break;
                }
            }
            catch { }

            // 主板 + BIOS
            try
            {
                string board = "";
                using var bd = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard").Get();
                foreach (var o in bd)
                {
                    board = $"{GetProp(o, "Product")} ({GetProp(o, "Manufacturer")})";
                    break;
                }
                string bios = "";
                using var bi = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS").Get();
                foreach (var o in bi)
                {
                    string ver = GetProp(o, "SMBIOSBIOSVersion")?.ToString() ?? "";
                    string date = "";
                    try
                    {
                        var d = ManagementDateTimeConverter.ToDateTime(GetProp(o, "ReleaseDate")?.ToString() ?? "");
                        date = d.ToString("yyyy-MM-dd");
                    }
                    catch { }
                    bios = $"BIOS {ver}" + (date != "" ? $" ({date})" : "");
                    break;
                }
                rows.Add(new HardwareRow { Label = "主板", Value = $"{board} · {bios}" });
            }
            catch { }

            // 显卡（过滤虚拟显示适配器）+ 显存
            try
            {
                var vramByNames = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    using var cls = Registry.LocalMachine.OpenSubKey(
                        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                    if (cls != null)
                    {
                        foreach (var sub in cls.GetSubKeyNames())
                        {
                            using var sk = cls.OpenSubKey(sub);
                            string desc = sk?.GetValue("DriverDesc")?.ToString() ?? "";
                            if (desc == "") continue;
                            object? q = sk?.GetValue("qwMemorySize");
                            if (q is long l) vramByNames[desc] = l;
                            else if (q is int i && i > 0) vramByNames[desc] = i;
                        }
                    }
                }
                catch { }

                var gpus = new List<string>();
                using var gpu = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController").Get();
                foreach (var o in gpu)
                {
                    string name = GetProp(o, "Name")?.ToString() ?? "";
                    if (IsVirtualGpu(name)) continue;
                    string vram = "";
                    if (vramByNames.TryGetValue(name, out long bytes) && bytes > 0)
                        vram = $" · 显存 {bytes / 1024 / 1024 / 1024.0:0.#} GB";
                    gpus.Add(name + vram);
                }
                if (gpus.Count > 0)
                    rows.Add(new HardwareRow { Label = "显卡", Value = string.Join("  +  ", gpus) });
            }
            catch { }

            // 内存
            try
            {
                using var ram = new ManagementObjectSearcher(
                    "SELECT Manufacturer, Capacity, ConfiguredClockSpeed, SMBIOSMemoryType FROM Win32_PhysicalMemory").Get();
                long total = 0;
                var details = new List<string>();
                foreach (var o in ram)
                {
                    long cap = Convert.ToInt64(GetProp(o, "Capacity") ?? 0L);
                    total += cap;
                    string mfr = (GetProp(o, "Manufacturer")?.ToString() ?? "").Trim();
                    int mhz = Convert.ToInt32(GetProp(o, "ConfiguredClockSpeed") ?? 0);
                    int smType = Convert.ToInt32(GetProp(o, "SMBIOSMemoryType") ?? 0);
                    string type = smType == 34 ? "DDR5" : smType == 26 ? "DDR4" : "";
                    details.Add($"{mfr} {cap / 1024 / 1024 / 1024.0:0.#}GB" + (type != "" ? $" {type}" : "") + (mhz > 0 ? $" {mhz}MHz" : ""));
                }
                if (total > 0)
                    rows.Add(new HardwareRow { Label = "内存", Value = $"共 {total / 1024 / 1024 / 1024.0:0.#} GB · {string.Join(" + ", details)}" });
            }
            catch { }

            // 硬盘
            try
            {
                using var dd = new ManagementObjectSearcher("SELECT Model, Size FROM Win32_DiskDrive").Get();
                var disks = dd.OfType<ManagementObject>()
                    .Select(o => $"{GetProp(o, "Model")} ({Convert.ToInt64(GetProp(o, "Size") ?? 0L) / 1024 / 1024 / 1024.0:0} GB)")
                    .ToList();
                if (disks.Count > 0)
                    rows.Add(new HardwareRow { Label = "硬盘", Value = string.Join("  +  ", disks) });
            }
            catch { }

            // 显示器
            try
            {
                Action<string> D2 = m => { try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "wmc_hw_diag.txt"), DateTime.Now.ToString("HH:mm:ss ") + m + "\r\n"); } catch { } };
                string monName = "";
                using var mon = new ManagementObjectSearcher(@"root\wmi", "SELECT UserFriendlyName, Active FROM WmiMonitorID").Get();
                foreach (var o in mon)
                {
                    if (!Equals(GetProp(o, "Active"), true)) continue;
                    var ids = GetProp(o, "UserFriendlyName") as ushort[];
                    if (ids == null) continue;
                    string n = new string(ids.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim();
                    if (n != "") { monName = n; break; }
                }
                string mode = "";
                using var vc = new ManagementObjectSearcher(
                    "SELECT VideoModeDescription, CurrentRefreshRate FROM Win32_VideoController").Get();
                foreach (var o in vc)
                {
                    string gname = GetProp(o, "Name")?.ToString() ?? "";
                    if (IsVirtualGpu(gname)) continue;
                    string vm = GetProp(o, "VideoModeDescription")?.ToString() ?? "";
                    var m = Regex.Match(vm, @"(\d+)\s*x\s*(\d+)");
                    if (!m.Success) continue;
                    int hz = Convert.ToInt32(GetProp(o, "CurrentRefreshRate") ?? 0);
                    mode = $"{m.Groups[1].Value}×{m.Groups[2].Value}" + (hz > 0 ? $" @ {hz}Hz" : "");
                    break;
                }
                if (mode == "")
                {
                    try
                    {
                        var dm = new DEVMODE();
                        dm.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
                        if (EnumDisplaySettings(null, -1, ref dm) && dm.dmPelsWidth > 0)
                            mode = $"{dm.dmPelsWidth}×{dm.dmPelsHeight}" +
                                   (dm.dmDisplayFrequency > 1 ? $" @ {dm.dmDisplayFrequency}Hz" : "");
                    }
                    catch { }
                }
                if (monName != "" || mode != "")
                    rows.Add(new HardwareRow { Label = "显示器", Value = $"{monName} · {mode}".Trim(' ', '·') });
            }
            catch (Exception ex) { Diag("monitor", ex); }

            return rows;
        }

        // ---------------- Device inventory (all devices, ghost-flagged) ----------------
        public static List<PhantomDevice> ScanAllDevices()
        {
            var list = new List<PhantomDevice>();
            using var searcher = new ManagementObjectSearcher("SELECT Name, PNPClass, DeviceID, Present, ConfigManagerErrorCode FROM Win32_PnPEntity");
            foreach (var o in searcher.Get())
            {
                bool present = Equals(GetPropPub(o, "Present"), true);
                int code = Convert.ToInt32(GetPropPub(o, "ConfigManagerErrorCode") ?? 0);
                bool ghost = !present || code == 45;
                list.Add(new PhantomDevice
                {
                    Name = o["Name"]?.ToString() ?? "(unknown)",
                    Class = o["PNPClass"]?.ToString() ?? "",
                    InstanceId = o["DeviceID"]?.ToString() ?? "",
                    IsGhost = ghost,
                    Selected = ghost
                });
            }
            return list.OrderBy(d => d.IsGhost ? 0 : 1).ThenBy(d => d.Class).ThenBy(d => d.Name).ToList();
        }

        static object? GetPropPub(ManagementBaseObject o, string name)
        {
            try { return o[name]; } catch { return null; }
        }

        // ---------------- Phantom devices ----------------
        public static List<PhantomDevice> ScanPhantomDevices()
        {
            var list = new List<PhantomDevice>();
            using var searcher = new ManagementObjectSearcher("SELECT Name, PNPClass, DeviceID, Present, ConfigManagerErrorCode FROM Win32_PnPEntity");
            foreach (var o in searcher.Get())
            {
                bool present = Equals(o["Present"], true);
                int code = Convert.ToInt32(o["ConfigManagerErrorCode"] ?? 0);
                // Present=false or CM_PROB_PHANTOM(45) / DISABLED-ish stale entries
                if (!present || code == 45)
                {
                    list.Add(new PhantomDevice
                    {
                        Name = o["Name"]?.ToString() ?? "(unknown)",
                        Class = o["PNPClass"]?.ToString() ?? "",
                        InstanceId = o["DeviceID"]?.ToString() ?? ""
                    });
                }
            }
            return list.OrderBy(d => d.Class).ThenBy(d => d.Name).ToList();
        }

        public static (int ok, int fail, List<string> errors) RemoveDevices(
            IEnumerable<PhantomDevice> devices, Action<string> progress)
        {
            int ok = 0, fail = 0;
            var errors = new List<string>();
            foreach (var d in devices)
            {
                progress($"正在移除：{d.Name}");
                var (code, _) = RunPnPUtil($"/remove-device \"{d.InstanceId}\"", 15000);
                if (code == 0) ok++; else { fail++; errors.Add($"{d.Name}: pnputil exit {code}"); }
            }
            return (ok, fail, errors);
        }

        // ---------------- Suspect services ----------------
        public static List<ServiceInfo> ScanSuspectServices()
        {
            var list = new List<ServiceInfo>();
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DisplayName, State, StartMode, PathName FROM Win32_Service");
            foreach (var o in searcher.Get())
            {
                string name = o["Name"]?.ToString() ?? "";
                string disp = o["DisplayName"]?.ToString() ?? "";
                string path = o["PathName"]?.ToString() ?? "";
                string hay = $"{name} {disp} {path}";
                string? reason = null;
                if (path.Contains("DriverStore", StringComparison.OrdinalIgnoreCase) &&
                    VendorKeywords.Any(k => hay.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    reason = "厂商驱动服务（DriverStore）";
                else if (VendorKeywords.Any(k => disp.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                                                 name.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    reason = "命中厂商关键字";
                if (reason != null)
                {
                    list.Add(new ServiceInfo
                    {
                        Name = name,
                        DisplayName = disp,
                        State = o["State"]?.ToString() ?? "",
                        StartMode = o["StartMode"]?.ToString() ?? "",
                        PathName = path,
                        Reason = reason
                    });
                }
            }
            return list.OrderBy(s => s.Name).ToList();
        }

        /// <summary>Delete a service via registry (no sc.exe dependency).</summary>
        public static bool DeleteService(string name)
        {
            try { StopService(name); } catch { }
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\" + name, writable: true);
            if (key == null) return false;
            try { Registry.LocalMachine.DeleteSubKeyTree(@"SYSTEM\CurrentControlSet\Services\" + name, false); return true; }
            catch { return false; }
        }

        public static bool SetServiceStartType(string name, int startType) // 3=manual 4=disabled
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\" + name, writable: true);
            if (key == null) return false;
            key.SetValue("Start", startType, RegistryValueKind.DWord);
            return true;
        }

        static void StopService(string name)
        {
            try
            {
                using var sc = new ServiceController(name);
                if (sc.Status != ServiceControllerStatus.Stopped)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(8));
                }
            }
            catch { }
        }

        // ---------------- Driver packages ----------------
        public static List<DriverPackage> ScanUnusedDriverPackages()
        {
            var (code, connected) = RunPnPUtil("/enum-devices /connected", 30000);
            var connectedText = connected;

            var (_, all) = RunPnPUtil("/enum-drivers", 30000);
            var packages = new List<DriverPackage>();
            var blocks = Regex.Split(all, @"\r?\n\s*\r?\n");
            foreach (var block in blocks)
            {
                var infMatches = Regex.Matches(block, @"(\S+\.inf)");
                if (infMatches.Count == 0) continue;
                string pub = infMatches.Select(m => m.Groups[1].Value)
                                       .FirstOrDefault(v => Regex.IsMatch(v, @"^oem\d+\.inf$")) ?? "";
                if (pub == "") continue;
                string orig = infMatches.Select(m => m.Groups[1].Value)
                                        .FirstOrDefault(v => !Regex.IsMatch(v, @"^oem\d+\.inf$")) ?? "";

                if (Regex.IsMatch(connectedText, Regex.Escape(pub))) continue; // in use by a connected device

                string version = Regex.Match(block, @"\d{2}/\d{2}/\d{4}\s+[\d.]+").Value;
                string provider = ExtractField(block, new[] { "Provider Name", "提供程序名称" });

                string risk;
                string lower = (orig + " " + provider).ToLowerInvariant();
                if (KeepPackages.Any(k => lower.Contains(k)))
                    risk = "保留"; // current platform suite
                else if (ProtectedSoftware.Any(k => lower.Contains(k)))
                    risk = "软件"; // belongs to installed software
                else
                    risk = "可清理";

                packages.Add(new DriverPackage
                {
                    PublishedName = pub,
                    OriginalName = orig,
                    Version = version,
                    Provider = provider,
                    Risk = risk
                });
            }
            return packages.OrderBy(p => p.Risk).ThenBy(p => p.PublishedName).ToList();
        }

        public static (int ok, int fail, List<string> errors) DeleteDriverPackages(
            IEnumerable<DriverPackage> packages, Action<string> progress)
        {
            int ok = 0, fail = 0;
            var errors = new List<string>();
            foreach (var p in packages)
            {
                progress($"正在删除：{p.PublishedName} ({p.OriginalName})");
                var (code, _) = RunPnPUtil($"/delete-driver {p.PublishedName} /uninstall /force", 30000);
                if (code == 0) ok++; else { fail++; errors.Add($"{p.PublishedName}: exit {code}"); }
            }
            return (ok, fail, errors);
        }

        // ---------------- helpers ----------------
        static string ExtractField(string text, string[] labels)
        {
            foreach (var line in text.Split('\n'))
            {
                foreach (var label in labels)
                {
                    int idx = line.IndexOf(label, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        var val = line[(idx + label.Length)..].TrimStart(':', ' ').Trim();
                        if (val.Length > 0) return val;
                    }
                }
            }
            return "";
        }

        public static (int code, string output) RunPnPUtil(string args, int timeoutMs)
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "pnputil.exe"),
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.GetEncoding(0)
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(timeoutMs);
            return (p.ExitCode, output);
        }
    }
}
