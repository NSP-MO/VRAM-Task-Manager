using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using VramTaskManager.Models;

namespace VramTaskManager.Services
{
    public class ProcessManagerService
    {
        private static readonly HashSet<string> SystemProtectedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "dwm", "dwm.exe",
            "csrss", "csrss.exe",
            "explorer", "explorer.exe",
            "system",
            "services", "services.exe",
            "lsass", "lsass.exe",
            "smss", "smss.exe",
            "winlogon", "winlogon.exe",
            "wininit", "wininit.exe",
            "fontdrvhost", "fontdrvhost.exe",
            "searchhost", "searchhost.exe",
            "shellexperiencehost", "shellexperiencehost.exe",
            "startmenuexperiencehost", "startmenuexperiencehost.exe",
            "runtimebroker", "runtimebroker.exe",
            "systemsettings", "systemsettings.exe",
            "taskmgr", "taskmgr.exe"
        };

        private static readonly HashSet<string> PythonComputeNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "python", "python.exe",
            "pythonw", "pythonw.exe",
            "ollama", "ollama.exe",
            "ollama_llama_server", "ollama_llama_server.exe",
            "vllm", "vllm.exe",
            "uvicorn", "uvicorn.exe",
            "torch", "torchrun", "torchrun.exe",
            "blender", "blender.exe",
            "unrealengine", "unrealengine.exe",
            "unity", "unity.exe"
        };

        private static readonly HashSet<string> WebBrowserNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "chrome", "chrome.exe",
            "brave", "brave.exe",
            "msedge", "msedge.exe",
            "firefox", "firefox.exe",
            "opera", "opera.exe",
            "vivaldi", "vivaldi.exe",
            "msedgewebview2", "msedgewebview2.exe"
        };

        public static bool IsRunningAsAdministrator()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        public bool IsSystemProtectedProcess(string processName, int pid)
        {
            if (pid <= 4) return true;
            return SystemProtectedNames.Contains(processName) ||
                   SystemProtectedNames.Contains(Path.GetFileName(processName));
        }

        public (ProcessCategory Category, string Badge, string Color, string BgColor) ClassifyProcess(string processName, bool hasWindow)
        {
            string cleanName = Path.GetFileNameWithoutExtension(processName).ToLowerInvariant();

            if (IsSystemProtectedProcess(processName, 0))
            {
                return (ProcessCategory.SystemProtected, "System Core", "#858585", "#252526");
            }

            if (PythonComputeNames.Contains(cleanName) || PythonComputeNames.Contains(processName))
            {
                return (ProcessCategory.PythonCompute, "Python / Compute", "#dcdcaa", "#3c3822");
            }

            if (WebBrowserNames.Contains(cleanName) || WebBrowserNames.Contains(processName))
            {
                return (ProcessCategory.WebBrowser, "Browser", "#4ec9b0", "#1d3b37");
            }

            if (hasWindow)
            {
                return (ProcessCategory.UserApplication, "User App", "#569cd6", "#1c324a");
            }

            return (ProcessCategory.BackgroundService, "Background", "#9cdcfe", "#1e293b");
        }

        public (bool Success, string Message) TerminateProcess(int pid, bool force = true)
        {
            string procName = $"PID {pid}";
            try
            {
                using var process = Process.GetProcessById(pid);
                procName = process.ProcessName;

                if (!force && process.MainWindowHandle != IntPtr.Zero)
                {
                    bool closed = process.CloseMainWindow();
                    if (closed && process.WaitForExit(1500))
                    {
                        return (true, $"Process '{procName}' (PID {pid}) closed gracefully.");
                    }
                }

                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
                return (true, $"Process '{procName}' (PID {pid}) terminated successfully.");
            }
            catch (ArgumentException)
            {
                return (true, $"Process '{procName}' (PID {pid}) has already exited.");
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Access Denied: Trigger native Windows UAC Elevation prompt via taskkill runas
                return TerminateWithElevatedTaskkill(pid, procName);
            }
            catch (Exception ex)
            {
                return (false, $"Error terminating PID {pid}: {ex.Message}");
            }
        }

        private (bool Success, string Message) TerminateWithElevatedTaskkill(int pid, string procName)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c taskkill /F /T /PID {pid}",
                    Verb = "runas", // Invokes Windows UAC Elevation prompt modal!
                    UseShellExecute = true
                };

                using var elevatedProc = Process.Start(psi);
                elevatedProc?.WaitForExit(3500);

                // Verify if process exited
                try
                {
                    using var check = Process.GetProcessById(pid);
                    return (false, $"Access Denied: Process '{procName}' (PID {pid}) requires Administrator privileges.");
                }
                catch (ArgumentException)
                {
                    return (true, $"Process '{procName}' (PID {pid}) terminated with administrator privileges.");
                }
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED (User rejected UAC)
            {
                return (false, "Administrator elevation was cancelled by the user.");
            }
            catch (Exception ex)
            {
                return (false, $"Administrator elevation failed: {ex.Message}");
            }
        }

        public (int SuccessCount, int FailedCount, long ReclaimedBytes) TerminateProcesses(IEnumerable<ProcessVramItem> items)
        {
            int success = 0;
            int failed = 0;
            long reclaimed = 0;
            var accessDeniedPids = new List<ProcessVramItem>();

            foreach (var item in items)
            {
                try
                {
                    using var proc = Process.GetProcessById(item.Pid);
                    if (IsSystemProtectedProcess(proc.ProcessName, item.Pid))
                    {
                        failed++;
                        continue;
                    }

                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(1000);
                    success++;
                    reclaimed += item.DedicatedVramBytes;
                }
                catch (ArgumentException)
                {
                    // Already exited
                    success++;
                    reclaimed += item.DedicatedVramBytes;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    accessDeniedPids.Add(item);
                }
                catch
                {
                    failed++;
                }
            }

            // If some processes required admin elevation, perform a single batched elevated taskkill
            if (accessDeniedPids.Count > 0)
            {
                string pidArgs = string.Join(" ", accessDeniedPids.Select(p => $"/PID {p.Pid}"));
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "taskkill.exe",
                        Arguments = $"/F /T {pidArgs}",
                        Verb = "runas", // Single UAC prompt for all elevated batch items
                        UseShellExecute = true,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };

                    using var elevated = Process.Start(psi);
                    elevated?.WaitForExit(4000);

                    foreach (var item in accessDeniedPids)
                    {
                        try
                        {
                            using var check = Process.GetProcessById(item.Pid);
                            failed++;
                        }
                        catch (ArgumentException)
                        {
                            success++;
                            reclaimed += item.DedicatedVramBytes;
                        }
                    }
                }
                catch
                {
                    failed += accessDeniedPids.Count;
                }
            }

            return (success, failed, reclaimed);
        }

        public (int TerminatedCount, long ReclaimedBytes) CleanAllPythonComputeProcesses(IEnumerable<ProcessVramItem> currentItems)
        {
            int count = 0;
            long reclaimed = 0;

            foreach (var item in currentItems)
            {
                if (item.Category == ProcessCategory.PythonCompute && !item.IsSystemProtected)
                {
                    var result = TerminateProcess(item.Pid, force: true);
                    if (result.Success)
                    {
                        count++;
                        reclaimed += item.DedicatedVramBytes;
                    }
                }
            }

            return (count, reclaimed);
        }
    }
}
