using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MemoryStressTester.Services;

namespace MemoryStressTester.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MemoryController : ControllerBase
{
    private readonly IMemoryStressService _memoryService;
    private readonly MemorySettings _settings;
    private readonly ILogger<MemoryController> _logger;

    public MemoryController(
        IMemoryStressService memoryService, 
        IOptions<MemorySettings> settings,
        ILogger<MemoryController> logger)
    {
        _memoryService = memoryService;
        _settings = settings.Value;
        _logger = logger;
    }

    [HttpPost("allocate")]
    public async Task<IActionResult> AllocateMemory([FromBody] MemoryAllocationRequest request)
    {
        try
        {
            // Enhanced validation
            if (request.MegabytesToAllocate <= 0)
            {
                return BadRequest(new { error = "Megabytes to allocate must be greater than 0" });
            }

            // Use settings-based limits instead of hardcoded values
            if (request.MegabytesToAllocate > _settings.MaxAllocationSizeMB)
            {
                return BadRequest(new { error = $"Allocation request too large (max {_settings.MaxAllocationSizeMB}MB)" });
            }

            var thresholdMB = request.ThresholdMB ?? _settings.DefaultThresholdMB;
            
            // Clamp threshold to max allowed
            thresholdMB = Math.Min(thresholdMB, _settings.MaxAllowedThresholdMB);

            _logger.LogInformation("Attempting to allocate {MB}MB with threshold {Threshold}MB", 
                request.MegabytesToAllocate, thresholdMB);

            var result = await _memoryService.AllocateMemoryAsync(request.MegabytesToAllocate, thresholdMB);

            // If allocation would exceed threshold or OOM occurred, return 500
            if (result.IsThresholdExceeded || result.IsOutOfMemory)
            {
                _logger.LogError("Memory allocation failed: {Message}", result.Message);
                
                // Return 500 Internal Server Error as requested
                return StatusCode(500, new
                {
                    error = "Memory allocation failed",
                    message = result.Message,
                    details = new
                    {
                        requestedMB = result.RequestedMB,
                        currentMemoryMB = result.CurrentMemoryMB,
                        thresholdMB = result.ThresholdMB,
                        isOutOfMemory = result.IsOutOfMemory,
                        isThresholdExceeded = result.IsThresholdExceeded
                    }
                });
            }

            _logger.LogInformation("Memory allocation successful: {Message}", result.Message);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during memory allocation");
            return StatusCode(500, new 
            { 
                error = "Internal server error", 
                message = "An unexpected error occurred during memory allocation" 
            });
        }
    }

    [HttpGet("status")]
    public IActionResult GetMemoryStatus()
    {
        try
        {
            var status = _memoryService.GetMemoryStatus();
            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting memory status");
            return StatusCode(500, new { error = "Failed to get memory status" });
        }
    }

    [HttpPost("clear")]
    public IActionResult ClearAllocations()
    {
        try
        {
            _memoryService.ClearAllocations();
            _logger.LogInformation("Memory allocations cleared");
            return Ok(new { message = "All allocations cleared successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing allocations");
            return StatusCode(500, new { error = "Failed to clear allocations" });
        }
    }

    [HttpGet("settings")]
    public IActionResult GetSettings()
    {
        return Ok(_settings);
    }

    [HttpPost("stress-test")]
    public async Task<IActionResult> StressTest([FromBody] StressTestRequest request)
    {
        try
        {
            // Enhanced validation for stress test
            if (request.Iterations <= 0)
            {
                return BadRequest(new { error = "Iterations must be greater than 0" });
            }

            if (request.MegabytesPerIteration <= 0)
            {
                return BadRequest(new { error = "Megabytes per iteration must be greater than 0" });
            }

            if (request.MegabytesPerIteration > _settings.MaxAllocationSizeMB)
            {
                return BadRequest(new { error = $"Allocation per iteration too large (max {_settings.MaxAllocationSizeMB}MB)" });
            }

            // Prevent stress tests that would exceed safe limits
            var totalRequestedMB = request.Iterations * request.MegabytesPerIteration;
            if (totalRequestedMB > _settings.MaxAllowedThresholdMB)
            {
                return BadRequest(new { error = $"Total stress test allocation ({totalRequestedMB}MB) exceeds safe limits. Consider reducing iterations or size per iteration." });
            }

            var results = new List<MemoryAllocationResult>();
            var thresholdMB = request.ThresholdMB ?? _settings.DefaultThresholdMB;

            _logger.LogInformation("Starting stress test with {Iterations} iterations of {MB}MB each (Total: {TotalMB}MB)", 
                request.Iterations, request.MegabytesPerIteration, totalRequestedMB);

            for (int i = 0; i < request.Iterations; i++)
            {
                var result = await _memoryService.AllocateMemoryAsync(request.MegabytesPerIteration, thresholdMB);
                results.Add(result);

                // If we hit threshold or OOM, stop and return 500
                if (result.IsThresholdExceeded || result.IsOutOfMemory)
                {
                    _logger.LogWarning("Stress test stopped at iteration {Iteration} due to: {Message}", 
                        i + 1, result.Message);
                    
                    return StatusCode(500, new
                    {
                        error = "Stress test failed",
                        message = $"Test stopped at iteration {i + 1}: {result.Message}",
                        completedIterations = i + 1,
                        totalIterations = request.Iterations,
                        results = results
                    });
                }

                // Small delay between allocations
                if (request.DelayBetweenAllocationsMs > 0)
                {
                    await Task.Delay(request.DelayBetweenAllocationsMs);
                }
            }

            return Ok(new
            {
                message = "Stress test completed successfully",
                completedIterations = results.Count,
                totalIterations = request.Iterations,
                results = results
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during stress test");
            return StatusCode(500, new { error = "Stress test failed with unexpected error" });
        }
    }
}

public class MemoryAllocationRequest
{
    public int MegabytesToAllocate { get; set; }
    public int? ThresholdMB { get; set; }
}

public class StressTestRequest
{
    public int Iterations { get; set; } = 5;
    public int MegabytesPerIteration { get; set; } = 100;
    public int? ThresholdMB { get; set; }
    public int DelayBetweenAllocationsMs { get; set; } = 100;
}
