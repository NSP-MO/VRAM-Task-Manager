using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace VramTaskManager.Models
{
    public enum ProcessCategory
    {
        All,
        PythonCompute,
        UserApplication,
        WebBrowser,
        SystemProtected,
        BackgroundService
    }

    public partial class ProcessVramItem : ObservableObject
    {
        public int Pid { get; set; }
        public string ProcessName { get; set; } = string.Empty;

        [ObservableProperty]
        private string _windowTitle = string.Empty;

        public string ExecutablePath { get; set; } = string.Empty;
        public ImageSource? Icon { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DedicatedVramMb))]
        [NotifyPropertyChangedFor(nameof(DedicatedVramGb))]
        [NotifyPropertyChangedFor(nameof(FormattedVram))]
        private long _dedicatedVramBytes;

        public double DedicatedVramMb => DedicatedVramBytes / (1024.0 * 1024.0);
        public double DedicatedVramGb => DedicatedVramMb / 1024.0;

        [ObservableProperty]
        private double _vramPercentage;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FormattedRam))]
        private double _systemRamMb;

        public ProcessCategory Category { get; set; }
        public string CategoryBadge { get; set; } = "Application";
        public string CategoryBadgeColor { get; set; } = "#007acc";
        public string CategoryBadgeBackground { get; set; } = "#1c324a";

        public bool IsSystemProtected { get; set; }

        [ObservableProperty]
        private bool _isSelected;

        public string FormattedVram => DedicatedVramMb >= 1024
            ? $"{DedicatedVramGb:F2} GB"
            : $"{DedicatedVramMb:F1} MB";

        public string FormattedRam => SystemRamMb >= 1024
            ? $"{SystemRamMb / 1024.0:F2} GB"
            : $"{SystemRamMb:F1} MB";

        public string DisplayTitle => !string.IsNullOrWhiteSpace(WindowTitle)
            ? WindowTitle
            : ProcessName;
    }
}
