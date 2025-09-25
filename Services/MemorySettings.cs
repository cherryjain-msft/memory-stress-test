namespace MemoryStressTester.Services;

public class MemorySettings
{
    public int DefaultThresholdMB { get; set; } = 1024;
    public int MaxAllowedThresholdMB { get; set; } = 4096;
    public int CleanupIntervalSeconds { get; set; } = 30;
    
    // New settings for bounding memory allocations
    public int MaxConcurrentAllocations { get; set; } = 50;
    public int MaxAllocationSizeMB { get; set; } = 512;
    public int LowMemoryEvictionThresholdMB { get; set; } = 1800; // Start evicting when memory usage exceeds this
}
