using System.ComponentModel;

namespace WinMigrationCleaner
{
    public class PhantomDevice : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public string Class { get; set; } = "";
        public string InstanceId { get; set; } = "";
        public bool Selected { get; set; } = true;

        public event PropertyChangedEventHandler? PropertyChanged;
        void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class ServiceInfo : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string State { get; set; } = "";
        public string StartMode { get; set; } = "";
        public string PathName { get; set; } = "";
        public string Reason { get; set; } = "";
        public bool Selected { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class DriverPackage : INotifyPropertyChanged
    {
        public string PublishedName { get; set; } = "";
        public string OriginalName { get; set; } = "";
        public string Version { get; set; } = "";
        public string Provider { get; set; } = "";
        public string Risk { get; set; } = "";   // 可清理 / 软件 / 保留
        public bool CanSelect => Risk == "可清理";
        public bool Selected { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class LogEntry
    {
        public string Time { get; set; } = DateTime.Now.ToString("HH:mm:ss");
        public string Message { get; set; } = "";
        public string Kind { get; set; } = "info"; // info / ok / warn / err
    }

    public class SystemSummary
    {
        public string Cpu { get; set; } = "—";
        public string Gpu { get; set; } = "—";
        public string Board { get; set; } = "—";
        public string Ram { get; set; } = "—";
    }
}
