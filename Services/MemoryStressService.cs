using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace MemoryStressTester.Services;

public interface IMemoryStressService
{
    Task<MemoryAllocationResult> AllocateMemoryAsync(int megabytes, int thresholdMB);
    MemoryStatus GetMemoryStatus();
    void ClearAllocations();
    bool IsAboveThreshold(int thresholdMB);
}

public class MemoryStressService : IMemoryStressService, IDisposable
{
    private readonly ConcurrentDictionary<Guid, byte[]> _allocatedMemory;
    private readonly Timer _cleanupTimer;
    private readonly object _lock = new();
    private readonly MemorySettings _settings;
    private bool _disposed = false;

    public MemoryStressService(IOptions<MemorySettings> settings)
    {
        _settings = settings.Value;
        _allocatedMemory = new ConcurrentDictionary<Guid, byte[]>();
        
        // More aggressive cleanup timer to prevent indefinite memory growth
        _cleanupTimer = new Timer(CleanupOldAllocations, null, 
            TimeSpan.FromSeconds(_settings.CleanupIntervalSeconds), 
            TimeSpan.FromSeconds(_settings.CleanupIntervalSeconds));
    }

    public async Task<MemoryAllocationResult> AllocateMemoryAsync(int megabytes, int thresholdMB)
    {
        try
        {
            var allocationId = Guid.NewGuid();
            var startMemory = GC.GetTotalMemory(false);
            var startTime = DateTime.UtcNow;

            // Validate allocation size against configured limits
            if (megabytes > _settings.MaxAllocationSizeMB)
            {
                return new MemoryAllocationResult
                {
                    Success = false,
                    AllocationId = allocationId,
                    RequestedMB = megabytes,
                    ThresholdMB = thresholdMB,
                    CurrentMemoryMB = GetCurrentMemoryUsageMB(),
                    Message = $"Allocation size {megabytes}MB exceeds maximum allowed {_settings.MaxAllocationSizeMB}MB",
                    IsThresholdExceeded = true
                };
            }

            // Check dictionary size limits to prevent unbounded growth
            if (_allocatedMemory.Count >= _settings.MaxConcurrentAllocations)
            {
                // Trigger aggressive cleanup before rejecting
                EvictOldestAllocations(_allocatedMemory.Count / 3);
                
                if (_allocatedMemory.Count >= _settings.MaxConcurrentAllocations)
                {
                    return new MemoryAllocationResult
                    {
                        Success = false,
                        AllocationId = allocationId,
                        RequestedMB = megabytes,
                        ThresholdMB = thresholdMB,
                        CurrentMemoryMB = GetCurrentMemoryUsageMB(),
                        Message = $"Maximum concurrent allocations ({_settings.MaxConcurrentAllocations}) reached. Clear existing allocations before creating new ones.",
                        IsThresholdExceeded = true
                    };
                }
            }

            // Check if we're about to exceed threshold
            var currentMemoryMB = GetCurrentMemoryUsageMB();
            if (currentMemoryMB + megabytes > thresholdMB)
            {
                return new MemoryAllocationResult
                {
                    Success = false,
                    AllocationId = allocationId,
                    RequestedMB = megabytes,
                    ThresholdMB = thresholdMB,
                    CurrentMemoryMB = currentMemoryMB,
                    Message = $"Allocation would exceed threshold of {thresholdMB}MB. Current: {currentMemoryMB}MB, Requested: {megabytes}MB",
                    IsThresholdExceeded = true
                };
            }

            // Proactive eviction if memory pressure is high
            if (currentMemoryMB > _settings.LowMemoryEvictionThresholdMB && _allocatedMemory.Count > 10)
            {
                EvictOldestAllocations(_allocatedMemory.Count / 4);
            }

            // Simulate memory allocation with LOH awareness
            await Task.Run(() =>
            {
                var bytes = new byte[megabytes * 1024 * 1024]; // Convert MB to bytes
                
                // Fill with random data to prevent optimization
                var random = new Random();
                random.NextBytes(bytes);
                
                _allocatedMemory[allocationId] = bytes;
            });

            var endMemory = GC.GetTotalMemory(false);
            var endTime = DateTime.UtcNow;

            var result = new MemoryAllocationResult
            {
                Success = true,
                AllocationId = allocationId,
                RequestedMB = megabytes,
                ActualMemoryIncreaseMB = (endMemory - startMemory) / (1024 * 1024),
                AllocationTimeMs = (endTime - startTime).TotalMilliseconds,
                ThresholdMB = thresholdMB,
                CurrentMemoryMB = GetCurrentMemoryUsageMB(),
                Message = $"Successfully allocated {megabytes}MB (Active allocations: {_allocatedMemory.Count})"
            };

            // Check if we're now above threshold after allocation
            result.IsThresholdExceeded = result.CurrentMemoryMB > thresholdMB;

            return result;
        }
        catch (OutOfMemoryException)
        {
            // Force aggressive cleanup on OOM
            EvictOldestAllocations(_allocatedMemory.Count / 2);
            
            // Force garbage collection
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            return new MemoryAllocationResult
            {
                Success = false,
                RequestedMB = megabytes,
                ThresholdMB = thresholdMB,
                CurrentMemoryMB = GetCurrentMemoryUsageMB(),
                Message = "Out of memory exception occurred during allocation. Some allocations were cleared.",
                IsOutOfMemory = true,
                IsThresholdExceeded = true
            };
        }
        catch (Exception ex)
        {
            return new MemoryAllocationResult
            {
                Success = false,
                RequestedMB = megabytes,
                ThresholdMB = thresholdMB,
                CurrentMemoryMB = GetCurrentMemoryUsageMB(),
                Message = $"Error during allocation: {ex.Message}",
                IsThresholdExceeded = true
            };
        }
    }

    public MemoryStatus GetMemoryStatus()
    {
        var totalMemory = GC.GetTotalMemory(false);
        var workingSet = Environment.WorkingSet;
        
        return new MemoryStatus
        {
            TotalAllocatedMB = totalMemory / (1024 * 1024),
            WorkingSetMB = workingSet / (1024 * 1024),
            ManagedMemoryMB = GC.GetTotalMemory(false) / (1024 * 1024),
            Generation0Collections = GC.CollectionCount(0),
            Generation1Collections = GC.CollectionCount(1),
            Generation2Collections = GC.CollectionCount(2),
            ActiveAllocations = _allocatedMemory.Count,
            LastCleanup = DateTime.UtcNow
        };
    }

    public bool IsAboveThreshold(int thresholdMB)
    {
        return GetCurrentMemoryUsageMB() > thresholdMB;
    }

    public void ClearAllocations()
    {
        lock (_lock)
        {
            _allocatedMemory.Clear();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private long GetCurrentMemoryUsageMB()
    {
        return GC.GetTotalMemory(false) / (1024 * 1024);
    }

    private void CleanupOldAllocations(object? state)
    {
        // More aggressive cleanup based on memory pressure
        var currentMemoryMB = GetCurrentMemoryUsageMB();
        var allocationCount = _allocatedMemory.Count;
        
        int itemsToRemove = 0;
        
        if (currentMemoryMB > _settings.LowMemoryEvictionThresholdMB)
        {
            // High memory pressure: remove 1/3 of allocations
            itemsToRemove = Math.Max(allocationCount / 3, 1);
        }
        else if (allocationCount > _settings.MaxConcurrentAllocations / 2)
        {
            // Moderate allocation count: remove 1/4
            itemsToRemove = Math.Max(allocationCount / 4, 1);
        }
        else if (allocationCount > 10)
        {
            // Light cleanup: remove a few old items
            itemsToRemove = Math.Min(5, allocationCount / 4);
        }
        
        if (itemsToRemove > 0)
        {
            EvictOldestAllocations(itemsToRemove);
            GC.Collect(0); // Light GC collection
        }
    }

    private void EvictOldestAllocations(int count)
    {
        if (count <= 0) return;

        var keysToRemove = _allocatedMemory.Keys.Take(count).ToList();
        foreach (var key in keysToRemove)
        {
            _allocatedMemory.TryRemove(key, out _);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _cleanupTimer?.Dispose();
            ClearAllocations();
            _disposed = true;
        }
    }
}

public class MemoryAllocationResult
{
    public bool Success { get; set; }
    public Guid AllocationId { get; set; }
    public int RequestedMB { get; set; }
    public long ActualMemoryIncreaseMB { get; set; }
    public double AllocationTimeMs { get; set; }
    public int ThresholdMB { get; set; }
    public long CurrentMemoryMB { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool IsThresholdExceeded { get; set; }
    public bool IsOutOfMemory { get; set; }
}

public class MemoryStatus
{
    public long TotalAllocatedMB { get; set; }
    public long WorkingSetMB { get; set; }
    public long ManagedMemoryMB { get; set; }
    public int Generation0Collections { get; set; }
    public int Generation1Collections { get; set; }
    public int Generation2Collections { get; set; }
    public int ActiveAllocations { get; set; }
    public DateTime LastCleanup { get; set; }
}
