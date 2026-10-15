using System.Diagnostics;
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

        // ---------------- System summary ----------------
        public static SystemSummary GetSystemSummary()
        {
            var s = new SystemSummary();
            try
            {
                using var cpu = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor").Get();
                foreach (var o in cpu) { s.Cpu = o["Name"]?.ToString() ?? "—"; break; }
            }
            catch { }
            try
            {
                using var gpu = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController").Get();
                var names = gpu.OfType<ManagementObject>()
                    .Select(o => o["Name"]?.ToString())
                    .Where(n => n != null && !n.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                    .Take(2);
                s.Gpu = string.Join(" + ", names);
            }
            catch { }
            try
            {
                using var bd = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard").Get();
                foreach (var o in bd)
                {
                    s.Board = $"{o["Manufacturer"]} {o["Product"]}";
                    break;
                }
            }
            catch { }
            try
            {
                using var ram = new ManagementObjectSearcher("SELECT Capacity FROM Win32_PhysicalMemory").Get();
                long total = ram.OfType<ManagementObject>()
                    .Sum(o => Convert.ToInt64(o["Capacity"] ?? 0L));
                if (total > 0) s.Ram = $"{total / 1024 / 1024 / 1024.0:0.#} GB";
            }
            catch { }
            return s;
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
