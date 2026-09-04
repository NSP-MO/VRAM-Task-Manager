using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VramTaskManager.Models;
using VramTaskManager.Services;

namespace VramTaskManager.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly GpuMonitoringService _gpuMonitoringService;
        private readonly ProcessManagerService _processManagerService;
        private readonly DispatcherTimer _timer;
        private readonly List<ProcessVramItem> _allProcesses = new();
        private bool _isSampling = false;

        // Hardware Telemetry
        [ObservableProperty]
        private string _gpuName = "Detecting GPU...";

        [ObservableProperty]
        private string _driverVersion = "---";

        [ObservableProperty]
        private double _totalVramMb = 0;

        [ObservableProperty]
        private double _usedVramMb = 0;

        [ObservableProperty]
        private double _freeVramMb = 0;

        [ObservableProperty]
        private double _vramUsagePercentage = 0;

        [ObservableProperty]
        private uint _gpuCoreUtil = 0;

        [ObservableProperty]
        private uint _gpuMemUtil = 0;

        [ObservableProperty]
        private uint _gpuTemperature = 0;

        [ObservableProperty]
        private uint _gpuPowerWatts = 0;

        [ObservableProperty]
        private uint _gpuFanSpeed = 0;

        // UI & Filter State
        [ObservableProperty]
        private string _searchQuery = string.Empty;

        [ObservableProperty]
        private ProcessCategory _selectedCategory = ProcessCategory.All;

        [ObservableProperty]
        private int _selectedVramThresholdIndex = 0; // 0: All, 1: >10MB, 2: >50MB, 3: >100MB, 4: >500MB, 5: >1GB

        [ObservableProperty]
        private int _selectedRefreshIntervalIndex = 1; // 0: 1s, 1: 2s, 2: 5s, 3: Paused

        [ObservableProperty]
        private bool _isAutoRefreshActive = true;

        [ObservableProperty]
        private bool _isLoading = false;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        [ObservableProperty]
        private string _statusMessageColor = "#858585";

        [ObservableProperty]
        private ProcessVramItem? _selectedProcess;

        [ObservableProperty]
        private int _totalProcessesCount = 0;

        [ObservableProperty]
        private int _filteredProcessesCount = 0;

        [ObservableProperty]
        private double _totalTrackedVramMb = 0;

        // Confirmation Modal
        [ObservableProperty]
        private bool _isConfirmModalVisible = false;

        [ObservableProperty]
        private string _confirmModalTitle = string.Empty;

        [ObservableProperty]
        private string _confirmModalMessage = string.Empty;

        [ObservableProperty]
        private string _confirmModalSeverity = "Warning"; // "Warning", "Danger", or "Info"

        [ObservableProperty]
        private string _confirmModalActionText = "Confirm";

        private Action? _pendingConfirmAction;

        [ObservableProperty]
        private bool _isRunningAsAdmin = false;

        public ObservableCollection<ProcessVramItem> FilteredProcesses { get; } = new();

        public MainViewModel()
        {
            _processManagerService = new ProcessManagerService();
            _gpuMonitoringService = new GpuMonitoringService(_processManagerService);
            IsRunningAsAdmin = ProcessManagerService.IsRunningAsAdministrator();

            // Configure default sort by Dedicated VRAM descending
            var view = CollectionViewSource.GetDefaultView(FilteredProcesses);
            view.SortDescriptions.Add(new SortDescription(nameof(ProcessVramItem.DedicatedVramBytes), ListSortDirection.Descending));

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _timer.Tick += async (s, e) => await SampleGpuDataAsync(isPeriodic: true);

            // Trigger initial sample
            _ = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            await SampleGpuDataAsync(isPeriodic: false);
            _timer.Start();
        }

        partial void OnSearchQueryChanged(string value) => ApplyFilter();
        partial void OnSelectedCategoryChanged(ProcessCategory value) => ApplyFilter();
        partial void OnSelectedVramThresholdIndexChanged(int value) => ApplyFilter();

        partial void OnSelectedRefreshIntervalIndexChanged(int value)
        {
            switch (value)
            {
                case 0:
                    _timer.Interval = TimeSpan.FromSeconds(1);
                    if (!IsAutoRefreshActive) IsAutoRefreshActive = true;
                    _timer.Start();
                    break;
                case 1:
                    _timer.Interval = TimeSpan.FromSeconds(2);
                    if (!IsAutoRefreshActive) IsAutoRefreshActive = true;
                    _timer.Start();
                    break;
                case 2:
                    _timer.Interval = TimeSpan.FromSeconds(5);
                    if (!IsAutoRefreshActive) IsAutoRefreshActive = true;
                    _timer.Start();
                    break;
                case 3:
                    IsAutoRefreshActive = false;
                    _timer.Stop();
                    break;
            }
        }

        [RelayCommand]
        public async Task RefreshAsync()
        {
            await SampleGpuDataAsync(isPeriodic: false);
        }

        [RelayCommand]
        public void ToggleAutoRefresh()
        {
            IsAutoRefreshActive = !IsAutoRefreshActive;
            if (IsAutoRefreshActive)
            {
                if (SelectedRefreshIntervalIndex == 3)
                    SelectedRefreshIntervalIndex = 1;
                _timer.Start();
                ShowStatus("Auto-refresh enabled.", "#89d185");
            }
            else
            {
                _timer.Stop();
                SelectedRefreshIntervalIndex = 3;
                ShowStatus("Auto-refresh paused.", "#cca700");
            }
        }

        [RelayCommand]
        public void SelectCategory(string categoryName)
        {
            if (Enum.TryParse<ProcessCategory>(categoryName, out var cat))
            {
                SelectedCategory = cat;
            }
        }

        private async Task SampleGpuDataAsync(bool isPeriodic)
        {
            if (_isSampling) return;
            _isSampling = true;

            if (!isPeriodic) IsLoading = true;

            try
            {
                var (telemetry, processes) = await _gpuMonitoringService.SampleGpuDataAsync();

                // Update Hardware Telemetry
                GpuName = telemetry.GpuName;
                DriverVersion = telemetry.DriverVersion;
                TotalVramMb = telemetry.TotalVramMb;
                UsedVramMb = telemetry.UsedVramMb;
                FreeVramMb = telemetry.FreeVramMb;
                VramUsagePercentage = telemetry.VramUsagePercentage;
                GpuCoreUtil = telemetry.GpuCoreUtilization;
                GpuMemUtil = telemetry.MemoryControllerUtilization;
                GpuTemperature = telemetry.TemperatureCelsius;
                GpuPowerWatts = telemetry.PowerWatts;
                GpuFanSpeed = telemetry.FanSpeedPercentage;

                // Update Process cache
                _allProcesses.Clear();
                _allProcesses.AddRange(processes);

                TotalProcessesCount = _allProcesses.Count;
                TotalTrackedVramMb = _allProcesses.Sum(p => p.DedicatedVramMb);

                ApplyFilter();
            }
            catch (Exception ex)
            {
                ShowStatus($"Telemetry sample error: {ex.Message}", "#f14c4c");
            }
            finally
            {
                _isSampling = false;
                if (!isPeriodic) IsLoading = false;
            }
        }

        private void ApplyFilter()
        {
            var query = SearchQuery.Trim();
            var thresholdBytes = GetThresholdBytes(SelectedVramThresholdIndex);

            var filtered = _allProcesses.Where(p =>
            {
                // VRAM Threshold check
                if (p.DedicatedVramBytes < thresholdBytes) return false;

                // Category check
                if (SelectedCategory != ProcessCategory.All && p.Category != SelectedCategory) return false;

                // Search Query check
                if (!string.IsNullOrEmpty(query))
                {
                    bool matchName = p.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase);
                    bool matchTitle = p.WindowTitle.Contains(query, StringComparison.OrdinalIgnoreCase);
                    bool matchPid = p.Pid.ToString().Contains(query, StringComparison.OrdinalIgnoreCase);
                    bool matchPath = p.ExecutablePath.Contains(query, StringComparison.OrdinalIgnoreCase);
                    if (!matchName && !matchTitle && !matchPid && !matchPath) return false;
                }

                return true;
            }).ToList();

            SynchronizeFilteredProcesses(filtered);
        }

        private void SynchronizeFilteredProcesses(List<ProcessVramItem> filtered)
        {
            var targetMap = new Dictionary<int, ProcessVramItem>(filtered.Count);
            foreach (var item in filtered)
            {
                targetMap[item.Pid] = item;
            }

            // 1. Remove deleted items (processes that exited)
            for (int i = FilteredProcesses.Count - 1; i >= 0; i--)
            {
                var existing = FilteredProcesses[i];
                if (!targetMap.ContainsKey(existing.Pid))
                {
                    FilteredProcesses.RemoveAt(i);
                }
            }

            // 2. In-place update existing items or insert new processes
            var existingMap = new Dictionary<int, ProcessVramItem>(FilteredProcesses.Count);
            foreach (var item in FilteredProcesses)
            {
                existingMap[item.Pid] = item;
            }

            int insertIndex = 0;
            foreach (var target in filtered)
            {
                if (existingMap.TryGetValue(target.Pid, out var existing))
                {
                    // Reactive field updates: Triggers only minimal property change notifications without DataGrid row destruction
                    existing.DedicatedVramBytes = target.DedicatedVramBytes;
                    existing.VramPercentage = target.VramPercentage;
                    existing.SystemRamMb = target.SystemRamMb;
                    if (existing.WindowTitle != target.WindowTitle)
                    {
                        existing.WindowTitle = target.WindowTitle;
                    }
                }
                else
                {
                    FilteredProcesses.Insert(Math.Min(insertIndex, FilteredProcesses.Count), target);
                }
                insertIndex++;
            }

            FilteredProcessesCount = FilteredProcesses.Count;
        }

        private long GetThresholdBytes(int index)
        {
            return index switch
            {
                1 => 10L * 1024 * 1024,      // > 10 MB
                2 => 50L * 1024 * 1024,      // > 50 MB
                3 => 100L * 1024 * 1024,     // > 100 MB
                4 => 500L * 1024 * 1024,     // > 500 MB
                5 => 1024L * 1024 * 1024,    // > 1 GB
                _ => 0L                      // All
            };
        }

        [RelayCommand]
        public void KillProcess(ProcessVramItem? item)
        {
            item ??= SelectedProcess;
            if (item == null) return;

            if (item.IsSystemProtected)
            {
                ShowConfirmModal(
                    title: "Protected System Component",
                    message: $"Process '{item.ProcessName}' (PID {item.Pid}) is a core Windows operating system component.\n\nTerminating it may cause Windows to become unstable, crash, or log off.\n\nAre you absolutely sure you want to proceed?",
                    severity: "Danger",
                    actionText: "Terminate Process",
                    action: () => ExecuteKillProcess(item)
                );
            }
            else
            {
                ShowConfirmModal(
                    title: "End Task",
                    message: $"Are you sure you want to end task for '{item.ProcessName}' (PID {item.Pid})?\n\nThis will free approximately {item.FormattedVram} of dedicated VRAM.",
                    severity: "Warning",
                    actionText: "End Task",
                    action: () => ExecuteKillProcess(item)
                );
            }
        }

        private void ExecuteKillProcess(ProcessVramItem item)
        {
            var (success, message) = _processManagerService.TerminateProcess(item.Pid, force: true);
            if (success)
            {
                ShowStatus($"Reclaimed ~{item.FormattedVram} VRAM from PID {item.Pid} ({item.ProcessName}).", "#89d185");
                _ = RefreshAsync();
            }
            else
            {
                ShowStatus(message, "#f14c4c");
            }
        }

        [RelayCommand]
        public void CleanPythonProcesses()
        {
            var pythonItems = _allProcesses.Where(p => p.Category == ProcessCategory.PythonCompute && !p.IsSystemProtected).ToList();
            if (pythonItems.Count == 0)
            {
                ShowStatus("No active Python / Compute processes detected.", "#858585");
                return;
            }

            double totalMb = pythonItems.Sum(p => p.DedicatedVramMb);
            string formattedTotal = totalMb >= 1024 ? $"{totalMb / 1024.0:F2} GB" : $"{totalMb:F1} MB";

            ShowConfirmModal(
                title: "Clean Python / Compute Engine Instances",
                message: $"Found {pythonItems.Count} active Python / Compute process(es) holding approximately {formattedTotal} of VRAM.\n\nDo you want to terminate all of them to reclaim VRAM?",
                severity: "Warning",
                actionText: "Clean Instances",
                action: () =>
                {
                    var (count, reclaimedBytes) = _processManagerService.CleanAllPythonComputeProcesses(pythonItems);
                    double reclaimedMb = reclaimedBytes / (1024.0 * 1024.0);
                    string reclaimedStr = reclaimedMb >= 1024 ? $"{reclaimedMb / 1024.0:F2} GB" : $"{reclaimedMb:F1} MB";
                    ShowStatus($"Terminated {count} Python processes. Reclaimed {reclaimedStr} VRAM.", "#89d185");
                    _ = RefreshAsync();
                }
            );
        }

        [RelayCommand]
        public void KillBatchSelected()
        {
            var selected = FilteredProcesses.Where(p => p.IsSelected).ToList();
            if (selected.Count == 0)
            {
                ShowStatus("No processes selected for batch termination.", "#858585");
                return;
            }

            double totalMb = selected.Sum(p => p.DedicatedVramMb);
            string formattedTotal = totalMb >= 1024 ? $"{totalMb / 1024.0:F2} GB" : $"{totalMb:F1} MB";

            ShowConfirmModal(
                title: "Batch Terminate Selected Processes",
                message: $"Terminate {selected.Count} selected process(es) and free approximately {formattedTotal} of VRAM?",
                severity: "Warning",
                actionText: "End Selected",
                action: () =>
                {
                    var (success, failed, reclaimedBytes) = _processManagerService.TerminateProcesses(selected);
                    double reclaimedMb = reclaimedBytes / (1024.0 * 1024.0);
                    string reclaimedStr = reclaimedMb >= 1024 ? $"{reclaimedMb / 1024.0:F2} GB" : $"{reclaimedMb:F1} MB";
                    ShowStatus($"Terminated {success} process(es). Reclaimed {reclaimedStr} VRAM. (Failed: {failed})", "#89d185");
                    _ = RefreshAsync();
                }
            );
        }

        [RelayCommand]
        public void ToggleSelectAll(bool isChecked)
        {
            foreach (var item in FilteredProcesses)
            {
                if (!item.IsSystemProtected)
                {
                    item.IsSelected = isChecked;
                }
            }
            // Trigger UI update
            ApplyFilter();
        }

        [RelayCommand]
        public void OpenProcessLocation(ProcessVramItem? item)
        {
            item ??= SelectedProcess;
            if (item == null) return;

            string exePath = item.ExecutablePath;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                exePath = ProcessPathHelper.GetExecutablePath(item.Pid, item.ProcessName);
                item.ExecutablePath = exePath;
            }

            if (string.IsNullOrEmpty(exePath))
            {
                ShowStatus($"Location not available for '{item.ProcessName}'.", "#cca700");
                return;
            }

            ShowConfirmModal(
                title: "Open File Location",
                message: $"Open Windows File Explorer for process '{item.ProcessName}' (PID {item.Pid})?\n\nTarget File:\n{exePath}",
                severity: "Info",
                actionText: "Open Location",
                action: () => ExecuteOpenProcessLocation(exePath)
            );
        }

        private void ExecuteOpenProcessLocation(string exePath)
        {
            try
            {
                if (File.Exists(exePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{exePath}\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    string dir = Path.GetDirectoryName(exePath) ?? string.Empty;
                    if (Directory.Exists(dir))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"\"{dir}\"",
                            UseShellExecute = true
                        });
                    }
                    else
                    {
                        ShowStatus($"Path not found: {exePath}", "#cca700");
                    }
                }
            }
            catch (Exception ex)
            {
                ShowStatus($"Failed to open file location: {ex.Message}", "#f14c4c");
            }
        }

        [RelayCommand]
        public void CopyProcessInfo(ProcessVramItem? item)
        {
            item ??= SelectedProcess;
            if (item == null) return;

            try
            {
                string info = $"Process: {item.ProcessName}\nPID: {item.Pid}\nVRAM: {item.FormattedVram}\nRAM: {item.FormattedRam}\nCategory: {item.CategoryBadge}\nPath: {item.ExecutablePath}";
                Clipboard.SetText(info);
                ShowStatus($"Copied info for PID {item.Pid} to clipboard.", "#89d185");
            }
            catch { }
        }

        private void ShowConfirmModal(string title, string message, string severity, string actionText, Action action)
        {
            ConfirmModalTitle = title;
            ConfirmModalMessage = message;
            ConfirmModalSeverity = severity;
            ConfirmModalActionText = actionText;
            _pendingConfirmAction = action;
            IsConfirmModalVisible = true;
        }

        [RelayCommand]
        public void ConfirmModalAccept()
        {
            var action = _pendingConfirmAction;
            _pendingConfirmAction = null;
            IsConfirmModalVisible = false;
            action?.Invoke();
        }

        [RelayCommand]
        public void ConfirmModalCancel()
        {
            IsConfirmModalVisible = false;
            _pendingConfirmAction = null;
        }

        [RelayCommand]
        public void RestartAsAdmin()
        {
            try
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                {
                    exePath = Process.GetCurrentProcess().MainModule?.FileName;
                }

                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Verb = "runas",
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                    Application.Current.Shutdown();
                }
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                ShowStatus("Administrator elevation was cancelled by user.", "#cca700");
            }
            catch (Exception ex)
            {
                ShowStatus($"Failed to restart as Administrator: {ex.Message}", "#f14c4c");
            }
        }

        private void ShowStatus(string message, string colorHex)
        {
            StatusMessage = message;
            StatusMessageColor = colorHex;
        }
    }
}
