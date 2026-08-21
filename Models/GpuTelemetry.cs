namespace VramTaskManager.Models
{
    public class GpuTelemetry
    {
        public string GpuName { get; set; } = "GPU Device";
        public string DriverVersion { get; set; } = "N/A";
        public ulong TotalVramBytes { get; set; }
        public ulong UsedVramBytes { get; set; }
        public ulong FreeVramBytes { get; set; }
        public double VramUsagePercentage => TotalVramBytes > 0 ? (double)UsedVramBytes / TotalVramBytes * 100.0 : 0;

        public double TotalVramMb => TotalVramBytes / (1024.0 * 1024.0);
        public double UsedVramMb => UsedVramBytes / (1024.0 * 1024.0);
        public double FreeVramMb => FreeVramBytes / (1024.0 * 1024.0);

        public double TotalVramGb => TotalVramMb / 1024.0;
        public double UsedVramGb => UsedVramMb / 1024.0;
        public double FreeVramGb => FreeVramMb / 1024.0;

        public uint GpuCoreUtilization { get; set; }
        public uint MemoryControllerUtilization { get; set; }
        public uint TemperatureCelsius { get; set; }
        public uint PowerWatts { get; set; }
        public uint FanSpeedPercentage { get; set; }
    }
}
