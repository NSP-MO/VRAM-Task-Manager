using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VramTaskManager.Services
{
    public static class ProcessPathHelper
    {
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        public static string GetExecutablePath(int processId, string processName = "")
        {
            // 1. High-privilege / low-overhead QueryFullProcessImageName via PROCESS_QUERY_LIMITED_INFORMATION
            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (hProcess != IntPtr.Zero)
            {
                try
                {
                    var sb = new StringBuilder(1024);
                    int size = sb.Capacity;
                    if (QueryFullProcessImageName(hProcess, 0, sb, ref size))
                    {
                        string path = sb.ToString();
                        if (File.Exists(path)) return path;
                    }
                }
                finally
                {
                    CloseHandle(hProcess);
                }
            }

            // 2. Standard .NET Process.MainModule fallback
            try
            {
                using var proc = Process.GetProcessById(processId);
                string path = proc.MainModule?.FileName ?? string.Empty;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    return path;
                }
            }
            catch { }

            // 3. Known System / Windows directory fallback
            if (!string.IsNullOrEmpty(processName))
            {
                string exeName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName : $"{processName}.exe";

                string system32 = Environment.SystemDirectory;
                string sysPath = Path.Combine(system32, exeName);
                if (File.Exists(sysPath)) return sysPath;

                string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string winPath = Path.Combine(winDir, exeName);
                if (File.Exists(winPath)) return winPath;
            }

            return string.Empty;
        }
    }
}
