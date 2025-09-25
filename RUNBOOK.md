# Memory Stress Tester - Operational Runbook

## Overview
This runbook provides operational procedures for managing the Memory Stress Tester application, specifically addressing OutOfMemoryException scenarios and memory management.

## Memory Management Safeguards

### Automatic Protections
The application includes several built-in safeguards to prevent OOM conditions:

- **Dictionary Size Limits**: Max 25 concurrent allocations (dev), 50 (prod)
- **Allocation Size Limits**: Max 256MB per allocation (dev), 512MB (prod) 
- **Proactive Eviction**: Automatic cleanup when memory usage exceeds thresholds
- **Cleanup Timer**: Periodic cleanup every 30 seconds (10 seconds in dev)

### Memory Thresholds by SKU
- **B1 (Dev)**: Default 512MB threshold, Max 2048MB
- **B2 (Prod)**: Default 1536MB threshold, Max 3072MB

## Emergency Procedures

### 1. High Memory Usage Alert Response
When memory usage approaches critical levels:

```bash
# Check current memory status
curl -X GET https://your-app.azurewebsites.net/api/memory/status

# Force immediate cleanup
curl -X POST https://your-app.azurewebsites.net/api/memory/admin/force-cleanup

# Clear all allocations if needed
curl -X POST https://your-app.azurewebsites.net/api/memory/clear
```

### 2. OutOfMemoryException Recovery
If the application experiences OOM:

1. **Immediate Response**:
   ```bash
   # Force aggressive cleanup
   curl -X POST https://your-app.azurewebsites.net/api/memory/admin/force-cleanup?itemsToRemove=50
   ```

2. **Scale Resources** (if needed):
   ```bash
   # Scale App Service Plan to higher SKU temporarily
   az appservice plan update --name memory-stress-tester-x4jnni --resource-group atlanta-sre-demo --sku B2
   ```

3. **Monitor Recovery**:
   ```bash
   # Monitor memory status
   watch -n 5 'curl -s https://your-app.azurewebsites.net/api/memory/status | jq .managedMemoryMB'
   ```

### 3. Stress Test Guidelines
Safe limits for stress testing:

**Development Environment (B1):**
- Max iterations: 10
- Max MB per iteration: 100MB
- Total allocation limit: 1000MB

**Production Environment (B2):**
- Max iterations: 20  
- Max MB per iteration: 200MB
- Total allocation limit: 3000MB

## Monitoring and Alerting

### Application Insights Queries
Monitor memory-related issues with these KQL queries:

```kql
// Memory allocation failures
traces
| where message contains "Memory allocation failed"
| summarize count() by bin(timestamp, 5m)
| render timechart

// High memory usage
performanceCounters
| where counterName == "Process(??APP_WIN32_PROC??)\\Working Set"
| summarize avg(counterValue) by bin(timestamp, 5m)
| render timechart

// OOM exceptions
exceptions
| where outerMessage contains "OutOfMemoryException"
| summarize count() by bin(timestamp, 1h)
| render barchart
```

### Key Metrics to Monitor
1. **Active Allocations**: Should not exceed configured limits
2. **Managed Memory**: Should stay below SKU-specific thresholds
3. **GC Collections**: High Gen 2 collections indicate memory pressure
4. **Application Response Time**: Degradation may indicate memory issues

## Configuration Management

### Environment-Specific Settings

**Development (appsettings.Development.json):**
```json
{
  "MemorySettings": {
    "DefaultThresholdMB": 512,
    "MaxAllowedThresholdMB": 2048,
    "MaxConcurrentAllocations": 25,
    "MaxAllocationSizeMB": 256,
    "LowMemoryEvictionThresholdMB": 900
  }
}
```

**Production (appsettings.json):**
```json
{
  "MemorySettings": {
    "DefaultThresholdMB": 1536,
    "MaxAllowedThresholdMB": 3072,
    "MaxConcurrentAllocations": 50,
    "MaxAllocationSizeMB": 512,
    "LowMemoryEvictionThresholdMB": 2048
  }
}
```

### Infrastructure Settings
- **B1 SKU**: 1.75GB RAM, suitable for development/testing
- **B2 SKU**: 3.5GB RAM, recommended for production workloads
- **AlwaysOn**: Enabled in production for consistent performance
- **Memory Limits**: Set via `WEBSITE_MEMORY_LIMIT_MB` app setting

## Troubleshooting

### Common Issues

1. **"Maximum concurrent allocations reached"**
   - **Cause**: Too many active allocations in memory
   - **Solution**: Call cleanup endpoint or increase `MaxConcurrentAllocations`

2. **"Allocation request too large"**
   - **Cause**: Single allocation exceeds `MaxAllocationSizeMB`
   - **Solution**: Reduce allocation size or increase limit for testing

3. **Consistent 500 errors on allocation**
   - **Cause**: Memory threshold consistently exceeded
   - **Solution**: Clear allocations, check for memory leaks, consider scaling

### Log Analysis
Look for these patterns in Application Insights:

- **Correlation IDs**: Track requests across the system
- **Memory pressure warnings**: Indicate approaching limits
- **Cleanup events**: Show automatic memory management actions

### Performance Optimization
1. **Tune cleanup intervals** based on allocation patterns
2. **Adjust eviction thresholds** for your workload
3. **Monitor GC pressure** and adjust generation limits
4. **Scale resources** if consistently hitting limits

## Contact Information
For issues related to memory management:
- **Primary**: SRE Team
- **Secondary**: Development Team
- **Emergency**: On-call rotation

## Revision History
- **v1.0**: Initial runbook after OOM incident resolution
- **Author**: SRE Memory Stress Agent
- **Last Updated**: 2025-01-22