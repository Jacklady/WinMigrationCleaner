using System.IO;
using System.Text.Json;

namespace WinMigrationCleaner
{
    public class ScanCache
    {
        public DateTime ScannedAt { get; set; }
        public List<PhantomDevice> Devices { get; set; } = new();
        public List<ServiceInfo> Services { get; set; } = new();
        public List<DriverPackage> Drivers { get; set; } = new();
    }

    /// <summary>Persists the last scan result so startup is instant. UI-agnostic.</summary>
    public static class CacheStore
    {
        static readonly string CacheFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinMigrationCleaner", "scan_cache.json");
        static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        public static ScanCache? Load()
        {
            try
            {
                if (!File.Exists(CacheFile)) return null;
                return JsonSerializer.Deserialize<ScanCache>(File.ReadAllText(CacheFile));
            }
            catch { return null; }
        }

        public static void Save(ScanCache cache)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
                File.WriteAllText(CacheFile, JsonSerializer.Serialize(cache, Options));
            }
            catch { }
        }
    }
}
