using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using VramTaskManager.Models;

namespace VramTaskManager.Services
{
    public class ProcessManagerService
    {
        private const uint TH32CS_SNAPPROCESS = 0x00000002;
        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint SE_PRIVILEGE_ENABLED = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID Luid;
            public uint Attributes;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, uint BufferLengthInBytes, IntPtr PreviousState, IntPtr ReturnLength);

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

        public ProcessManagerService()
        {
            EnableDebugPrivilege();
        }

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

        public static void EnableDebugPrivilege()
        {
            try
            {
                if (!IsRunningAsAdministrator()) return;

                if (OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr hToken))
                {
                    try
                    {
                        if (LookupPrivilegeValue(null, "SeDebugPrivilege", out LUID luid))
                        {
                            TOKEN_PRIVILEGES tp = new()
                            {
                                PrivilegeCount = 1,
                                Luid = luid,
                                Attributes = SE_PRIVILEGE_ENABLED
                            };
                            AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                        }
                    }
                    finally
                    {
                        CloseHandle(hToken);
                    }
                }
            }
            catch { }
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

        /// <summary>
        /// Finds the root ancestor process ID that shares the same executable name.
        /// This ensures parent watchdogs / host supervisors are terminated alongside the child worker,
        /// preventing auto-recovery or crash-restart loops.
        /// </summary>
        public static int FindRootProcessId(int targetPid, string targetProcessName)
        {
            if (targetPid <= 4) return targetPid;

            try
            {
                var parentMap = new Dictionary<int, (int ParentPid, string ExeName)>();
                IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
                if (snapshot != IntPtr.Zero && snapshot != (IntPtr)(-1))
                {
                    try
                    {
                        var pe = new PROCESSENTRY32();
                        pe.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>();
                        if (Process32First(snapshot, ref pe))
                        {
                            do
                            {
                                parentMap[(int)pe.th32ProcessID] = ((int)pe.th32ParentProcessID, pe.szExeFile);
                            } while (Process32Next(snapshot, ref pe));
                        }
                    }
                    finally
                    {
                        CloseHandle(snapshot);
                    }
                }

                string cleanTarget = Path.GetFileNameWithoutExtension(targetProcessName);
                int curr = targetPid;
                var visited = new HashSet<int> { curr };

                while (parentMap.TryGetValue(curr, out var info))
                {
                    int parentPid = info.ParentPid;
                    if (parentPid <= 4 || visited.Contains(parentPid))
                        break;

                    if (parentMap.TryGetValue(parentPid, out var parentInfo))
                    {
                        string cleanParent = Path.GetFileNameWithoutExtension(parentInfo.ExeName);
                        // Follow up the chain if parent is the same executable family
                        if (cleanParent.Equals(cleanTarget, StringComparison.OrdinalIgnoreCase))
                        {
                            curr = parentPid;
                            visited.Add(curr);
                            continue;
                        }
                    }
                    break;
                }

                return curr;
            }
            catch
            {
                return targetPid;
            }
        }

        /// <summary>
        /// Forcefully terminates a process and its entire hierarchy using Administrator privileges.
        /// Bypasses crashpad / error reporting handlers so the target process cannot restart or recover.
        /// </summary>
        public (bool Success, string Message) TerminateProcess(int pid, bool force = true)
        {
            string procName = $"PID {pid}";
            try
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    procName = process.ProcessName;
                }
                catch (ArgumentException)
                {
                    return (true, $"Process '{procName}' (PID {pid}) has already exited.");
                }

                // Identify root process to terminate the entire watchdog/supervisor tree
                int rootPid = FindRootProcessId(pid, procName);
                string pidArgs = (rootPid != pid) ? $"/PID {rootPid} /PID {pid}" : $"/PID {pid}";

                bool isElevated = IsRunningAsAdministrator();
                string taskkillPath = Path.Combine(Environment.SystemDirectory, "taskkill.exe");
                if (!File.Exists(taskkillPath)) taskkillPath = "taskkill.exe";

                var psi = new ProcessStartInfo
                {
                    FileName = taskkillPath,
                    Arguments = $"/F /T {pidArgs}",
                    Verb = isElevated ? "" : "runas", // Invokes Windows UAC Elevation prompt modal if not running as admin
                    UseShellExecute = !isElevated,
                    CreateNoWindow = isElevated,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var killProc = Process.Start(psi);
                killProc?.WaitForExit(4000);

                // Verify that the process and its memory allocation have truly terminated
                bool exited = false;
                for (int i = 0; i < 15; i++)
                {
                    try
                    {
                        using var check = Process.GetProcessById(pid);
                        if (check.HasExited)
                        {
                            exited = true;
                            break;
                        }
                        Thread.Sleep(100);
                    }
                    catch (ArgumentException)
                    {
                        exited = true;
                        break;
                    }
                }

                if (exited)
                {
                    return (true, $"Process '{procName}' (PID {pid}) terminated successfully with administrator privileges.");
                }
                else
                {
                    return (false, $"Process '{procName}' (PID {pid}) could not be terminated. It may be protected by an active system service or kernel driver.");
                }
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED (User cancelled UAC)
            {
                return (false, "Administrator elevation was cancelled by user.");
            }
            catch (Exception ex)
            {
                return (false, $"Error terminating PID {pid}: {ex.Message}");
            }
        }

        /// <summary>
        /// Batch terminates multiple processes with a single administrative taskkill invocation.
        /// </summary>
        public (int SuccessCount, int FailedCount, long ReclaimedBytes) TerminateProcesses(IEnumerable<ProcessVramItem> items)
        {
            var itemList = items.Where(item => !IsSystemProtectedProcess(item.ProcessName, item.Pid)).ToList();
            if (itemList.Count == 0) return (0, 0, 0);

            var pidsToKill = new HashSet<int>();
            foreach (var item in itemList)
            {
                int rootPid = FindRootProcessId(item.Pid, item.ProcessName);
                pidsToKill.Add(rootPid);
                pidsToKill.Add(item.Pid);
            }

            string pidArgs = string.Join(" ", pidsToKill.Select(p => $"/PID {p}"));
            bool isElevated = IsRunningAsAdministrator();
            string taskkillPath = Path.Combine(Environment.SystemDirectory, "taskkill.exe");
            if (!File.Exists(taskkillPath)) taskkillPath = "taskkill.exe";

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = taskkillPath,
                    Arguments = $"/F /T {pidArgs}",
                    Verb = isElevated ? "" : "runas", // Single UAC prompt for the entire batch
                    UseShellExecute = !isElevated,
                    CreateNoWindow = isElevated,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var killProc = Process.Start(psi);
                killProc?.WaitForExit(5000);
            }
            catch
            {
                // Elevation cancelled or failed
            }

            int success = 0;
            int failed = 0;
            long reclaimed = 0;

            foreach (var item in itemList)
            {
                bool exited = false;
                try
                {
                    using var check = Process.GetProcessById(item.Pid);
                    exited = check.HasExited;
                }
                catch (ArgumentException)
                {
                    exited = true;
                }

                if (exited)
                {
                    success++;
                    reclaimed += item.DedicatedVramBytes;
                }
                else
                {
                    failed++;
                }
            }

            return (success, failed, reclaimed);
        }

        public (int TerminatedCount, long ReclaimedBytes) CleanAllPythonComputeProcesses(IEnumerable<ProcessVramItem> currentItems)
        {
            var pythonItems = currentItems.Where(p => p.Category == ProcessCategory.PythonCompute && !p.IsSystemProtected).ToList();
            if (pythonItems.Count == 0) return (0, 0);

            var (success, _, reclaimed) = TerminateProcesses(pythonItems);
            return (success, reclaimed);
        }
    }
}
