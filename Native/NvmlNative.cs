using System;
using System.Runtime.InteropServices;
using System.Text;

namespace VramTaskManager.Native
{
    public static class NvmlNative
    {
        private const string NvmlDll = "nvml.dll";
        private static bool _isInitialized = false;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport(NvmlDll, EntryPoint = "nvmlInit_v2")]
        private static extern int nvmlInit_v2();

        [DllImport(NvmlDll, EntryPoint = "nvmlShutdown")]
        private static extern int nvmlShutdown();

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetCount_v2")]
        private static extern int nvmlDeviceGetCount_v2(out uint count);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
        private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetName")]
        private static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);

        [StructLayout(LayoutKind.Sequential)]
        public struct NvmlMemory
        {
            public ulong Total;
            public ulong Free;
            public ulong Used;
        }

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetMemoryInfo")]
        private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);

        [StructLayout(LayoutKind.Sequential)]
        public struct NvmlUtilization
        {
            public uint Gpu;
            public uint Memory;
        }

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetUtilizationRates")]
        private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetTemperature")]
        private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensorType, out uint temp);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetPowerUsage")]
        private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint powerMilliWatts);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetFanSpeed")]
        private static extern int nvmlDeviceGetFanSpeed(IntPtr device, out uint fanSpeed);

        [DllImport(NvmlDll, EntryPoint = "nvmlSystemGetDriverVersion")]
        private static extern int nvmlSystemGetDriverVersion(StringBuilder version, uint length);

        public static bool Initialize()
        {
            if (_isInitialized) return true;

            try
            {
                // Ensure nvml.dll can be found in system path or standard locations
                IntPtr hModule = LoadLibrary("nvml.dll");
                if (hModule == IntPtr.Zero)
                {
                    string systemPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvml.dll");
                    if (System.IO.File.Exists(systemPath))
                    {
                        LoadLibrary(systemPath);
                    }
                }

                int res = nvmlInit_v2();
                if (res == 0)
                {
                    _isInitialized = true;
                    return true;
                }
            }
            catch
            {
                _isInitialized = false;
            }

            return false;
        }

        public static void Shutdown()
        {
            if (_isInitialized)
            {
                try
                {
                    nvmlShutdown();
                }
                catch { }
                _isInitialized = false;
            }
        }

        public static bool GetGpuDeviceInfo(int index, out string name, out string driverVersion, out NvmlMemory memory, out NvmlUtilization util, out uint temp, out uint powerWatts, out uint fanSpeed)
        {
            name = "GPU Device";
            driverVersion = "Unknown";
            memory = default;
            util = default;
            temp = 0;
            powerWatts = 0;
            fanSpeed = 0;

            if (!_isInitialized && !Initialize()) return false;

            try
            {
                var sbVer = new StringBuilder(64);
                if (nvmlSystemGetDriverVersion(sbVer, 64) == 0)
                {
                    driverVersion = sbVer.ToString();
                }

                if (nvmlDeviceGetCount_v2(out uint count) == 0 && count > index)
                {
                    if (nvmlDeviceGetHandleByIndex_v2((uint)index, out IntPtr device) == 0)
                    {
                        var sbName = new StringBuilder(64);
                        if (nvmlDeviceGetName(device, sbName, 64) == 0)
                        {
                            name = sbName.ToString();
                        }

                        nvmlDeviceGetMemoryInfo(device, out memory);
                        nvmlDeviceGetUtilizationRates(device, out util);
                        nvmlDeviceGetTemperature(device, 0, out temp);

                        if (nvmlDeviceGetPowerUsage(device, out uint powerMw) == 0)
                        {
                            powerWatts = powerMw / 1000;
                        }

                        nvmlDeviceGetFanSpeed(device, out fanSpeed);
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }
    }
}
