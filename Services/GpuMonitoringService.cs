using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using VramTaskManager.Models;
using VramTaskManager.Native;

namespace VramTaskManager.Services
{
    public class GpuMonitoringService
    {
        private readonly ProcessManagerService _processManager;
        private PerformanceCounterCategory? _gpuCategory;

        private class CachedProcMeta
        {
            public string ProcessName { get; set; } = "Unknown";
            public string WindowTitle { get; set; } = string.Empty;
            public string ExecutablePath { get; set; } = string.Empty;
            public ImageSource? Icon { get; set; }
            public ProcessCategory Category { get; set; }
            public string CategoryBadge { get; set; } = "Application";
            public string CategoryBadgeColor { get; set; } = "#007acc";
            public string CategoryBadgeBackground { get; set; } = "#1c324a";
            public bool IsProtected { get; set; }
            public int TitleRefreshCounter { get; set; } = 0;
        }

        private readonly ConcurrentDictionary<int, CachedProcMeta> _metaCache = new();

        public GpuMonitoringService(ProcessManagerService processManager)
        {
            _processManager = processManager;
            NvmlNative.Initialize();
        }

        public async Task<(GpuTelemetry Telemetry, List<ProcessVramItem> Processes)> SampleGpuDataAsync()
        {
            return await Task.Run(() =>
            {
                // 1. Fetch Global Hardware Telemetry via NVML
                var telemetry = new GpuTelemetry();

                bool nvmlSuccess = NvmlNative.GetGpuDeviceInfo(
                    0,
                    out string gpuName,
                    out string driverVer,
                    out var memory,
                    out var util,
                    out uint temp,
                    out uint powerWatts,
                    out uint fanSpeed);

                if (nvmlSuccess)
                {
                    telemetry.GpuName = gpuName;
                    telemetry.DriverVersion = driverVer;
                    telemetry.TotalVramBytes = memory.Total;
                    telemetry.UsedVramBytes = memory.Used;
                    telemetry.FreeVramBytes = memory.Free;
                    telemetry.GpuCoreUtilization = util.Gpu;
                    telemetry.MemoryControllerUtilization = util.Memory;
                    telemetry.TemperatureCelsius = temp;
                    telemetry.PowerWatts = powerWatts;
                    telemetry.FanSpeedPercentage = fanSpeed;
                }

                // 2. Query Per-Process GPU Dedicated Memory efficiently
                var processMemoryMap = new Dictionary<int, long>();

                try
                {
                    _gpuCategory ??= new PerformanceCounterCategory("GPU Process Memory");
                    var categoryData = _gpuCategory.ReadCategory();

                    if (categoryData.Contains("Dedicated Usage"))
                    {
                        var dedicatedUsage = categoryData["Dedicated Usage"];
                        foreach (DictionaryEntry entry in dedicatedUsage)
                        {
                            if (entry.Key is not string instance || !instance.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)) continue;

                            var parts = instance.Split('_');
                            if (parts.Length < 2 || !int.TryParse(parts[1], out int pid)) continue;

                            if (entry.Value is InstanceData instanceData)
                            {
                                long bytes = instanceData.RawValue;
                                if (bytes > 0)
                                {
                                    if (processMemoryMap.ContainsKey(pid))
                                        processMemoryMap[pid] += bytes;
                                    else
                                        processMemoryMap[pid] = bytes;
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Fallback to iterative sampling if ReadCategory encounters permission/format variances
                    try
                    {
                        if (_gpuCategory != null)
                        {
                            string[] instanceNames = _gpuCategory.GetInstanceNames();
                            foreach (var instance in instanceNames)
                            {
                                if (!instance.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)) continue;
                                var parts = instance.Split('_');
                                if (parts.Length < 2 || !int.TryParse(parts[1], out int pid)) continue;

                                try
                                {
                                    using var counter = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", instance, true);
                                    long bytes = counter.RawValue;
                                    if (bytes > 0)
                                    {
                                        if (processMemoryMap.ContainsKey(pid))
                                            processMemoryMap[pid] += bytes;
                                        else
                                            processMemoryMap[pid] = bytes;
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }

                // 3. If NVML didn't return total VRAM, estimate from sum or fallback
                if (telemetry.TotalVramBytes == 0)
                {
                    long totalDedicated = processMemoryMap.Values.Sum();
                    telemetry.UsedVramBytes = (ulong)Math.Max(0, totalDedicated);
                    telemetry.TotalVramBytes = (ulong)Math.Max(8L * 1024 * 1024 * 1024, totalDedicated);
                    telemetry.FreeVramBytes = telemetry.TotalVramBytes > telemetry.UsedVramBytes
                        ? telemetry.TotalVramBytes - telemetry.UsedVramBytes
                        : 0;
                }

                // 4. Resolve Process Details using Fast Metadata Cache
                var resultList = new List<ProcessVramItem>(processMemoryMap.Count);
                ulong totalGpuBytes = telemetry.TotalVramBytes > 0 ? telemetry.TotalVramBytes : 1;
                var currentPids = new HashSet<int>(processMemoryMap.Keys);

                // Prune dead process caches
                foreach (var cachedPid in _metaCache.Keys)
                {
                    if (!currentPids.Contains(cachedPid))
                    {
                        _metaCache.TryRemove(cachedPid, out _);
                    }
                }

                foreach (var kvp in processMemoryMap)
                {
                    int pid = kvp.Key;
                    long vramBytes = kvp.Value;

                    if (!_metaCache.TryGetValue(pid, out var meta))
                    {
                        meta = new CachedProcMeta();
                        try
                        {
                            using var proc = Process.GetProcessById(pid);
                            meta.ProcessName = proc.ProcessName;
                            meta.WindowTitle = proc.MainWindowTitle;
                            bool hasWindow = proc.MainWindowHandle != IntPtr.Zero;

                            meta.ExecutablePath = ProcessPathHelper.GetExecutablePath(pid, meta.ProcessName);
                            meta.Icon = IconHelper.GetProcessIcon(meta.ExecutablePath, meta.ProcessName);
                            var (category, badge, badgeColor, badgeBg) = _processManager.ClassifyProcess(meta.ProcessName, hasWindow);
                            meta.Category = category;
                            meta.CategoryBadge = badge;
                            meta.CategoryBadgeColor = badgeColor;
                            meta.CategoryBadgeBackground = badgeBg;
                            meta.IsProtected = _processManager.IsSystemProtectedProcess(meta.ProcessName, pid);
                        }
                        catch
                        {
                            meta.ProcessName = $"Process ({pid})";
                        }

                        _metaCache[pid] = meta;
                    }
                    else
                    {
                        // Periodically refresh window title every ~5 ticks
                        meta.TitleRefreshCounter++;
                        if (meta.TitleRefreshCounter > 5)
                        {
                            meta.TitleRefreshCounter = 0;
                            try
                            {
                                using var proc = Process.GetProcessById(pid);
                                meta.WindowTitle = proc.MainWindowTitle;
                            }
                            catch { }
                        }
                    }

                    double ramMb = 0;
                    try
                    {
                        using var proc = Process.GetProcessById(pid);
                        ramMb = proc.WorkingSet64 / (1024.0 * 1024.0);
                    }
                    catch { }

                    double pct = (double)vramBytes / totalGpuBytes * 100.0;

                    var item = new ProcessVramItem
                    {
                        Pid = pid,
                        ProcessName = meta.ProcessName,
                        WindowTitle = meta.WindowTitle,
                        ExecutablePath = meta.ExecutablePath,
                        Icon = meta.Icon,
                        DedicatedVramBytes = vramBytes,
                        VramPercentage = pct,
                        SystemRamMb = ramMb,
                        Category = meta.Category,
                        CategoryBadge = meta.CategoryBadge,
                        CategoryBadgeColor = meta.CategoryBadgeColor,
                        CategoryBadgeBackground = meta.CategoryBadgeBackground,
                        IsSystemProtected = meta.IsProtected
                    };

                    resultList.Add(item);
                }

                // Sort by VRAM consumption descending
                resultList.Sort((a, b) => b.DedicatedVramBytes.CompareTo(a.DedicatedVramBytes));

                return (telemetry, resultList);
            });
        }
    }
}
