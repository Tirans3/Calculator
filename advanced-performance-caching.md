# Advanced Performance & Caching in ASP.NET Core

## 1. Memory Caching Strategies

### Advanced Memory Cache Implementation
```csharp
public interface IAdvancedMemoryCache
{
    Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> getItem, TimeSpan cacheDuration);
    Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> getItem, MemoryCacheEntryOptions options);
    void Remove(string key);
    void RemoveByPattern(string pattern);
    void Clear();
}

public class AdvancedMemoryCache : IAdvancedMemoryCache
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<AdvancedMemoryCache> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new();
    private readonly HashSet<string> _keys = new();
    private readonly object _keysLock = new();

    public AdvancedMemoryCache(IMemoryCache cache, ILogger<AdvancedMemoryCache> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> getItem, TimeSpan cacheDuration)
    {
        var options = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = cacheDuration,
            Priority = CacheItemPriority.Normal
        };

        return await GetOrSetAsync(key, getItem, options);
    }

    public async Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> getItem, MemoryCacheEntryOptions options)
    {
        if (_cache.TryGetValue(key, out T cachedValue))
        {
            _logger.LogDebug("Cache hit for key: {Key}", key);
            return cachedValue;
        }

        var semaphore = _semaphores.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        await semaphore.WaitAsync();
        try
        {
            // Double-check pattern
            if (_cache.TryGetValue(key, out cachedValue))
            {
                return cachedValue;
            }

            _logger.LogDebug("Cache miss for key: {Key}", key);
            var value = await getItem();

            if (value != null)
            {
                options.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration
                {
                    EvictionCallback = OnEviction,
                    State = key
                });

                _cache.Set(key, value, options);
                AddKey(key);
                
                _logger.LogDebug("Cached value for key: {Key}", key);
            }

            return value;
        }
        finally
        {
            semaphore.Release();
        }
    }

    public void Remove(string key)
    {
        _cache.Remove(key);
        RemoveKey(key);
        _logger.LogDebug("Removed cache entry for key: {Key}", key);
    }

    public void RemoveByPattern(string pattern)
    {
        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var keysToRemove = new List<string>();

        lock (_keysLock)
        {
            keysToRemove.AddRange(_keys.Where(key => regex.IsMatch(key)));
        }

        foreach (var key in keysToRemove)
        {
            Remove(key);
        }

        _logger.LogDebug("Removed {Count} cache entries matching pattern: {Pattern}", keysToRemove.Count, pattern);
    }

    public void Clear()
    {
        lock (_keysLock)
        {
            foreach (var key in _keys.ToList())
            {
                _cache.Remove(key);
            }
            _keys.Clear();
        }

        _logger.LogDebug("Cleared all cache entries");
    }

    private void OnEviction(object key, object value, EvictionReason reason, object state)
    {
        var cacheKey = state as string;
        if (cacheKey != null)
        {
            RemoveKey(cacheKey);
            _semaphores.TryRemove(cacheKey, out _);
        }

        _logger.LogDebug("Cache entry evicted for key: {Key}, reason: {Reason}", cacheKey, reason);
    }

    private void AddKey(string key)
    {
        lock (_keysLock)
        {
            _keys.Add(key);
        }
    }

    private void RemoveKey(string key)
    {
        lock (_keysLock)
        {
            _keys.Remove(key);
        }
    }
}
```

### Cache-Aside Pattern Implementation
```csharp
public class CacheAsideService<T> where T : class
{
    private readonly IAdvancedMemoryCache _cache;
    private readonly ILogger<CacheAsideService<T>> _logger;
    private readonly string _prefix;

    public CacheAsideService(IAdvancedMemoryCache cache, ILogger<CacheAsideService<T>> logger)
    {
        _cache = cache;
        _logger = logger;
        _prefix = typeof(T).Name.ToLower();
    }

    public async Task<T> GetAsync(string key, Func<Task<T>> factory, TimeSpan? cacheDuration = null)
    {
        var cacheKey = $"{_prefix}:{key}";
        var duration = cacheDuration ?? TimeSpan.FromMinutes(15);

        return await _cache.GetOrSetAsync(cacheKey, factory, duration);
    }

    public async Task<T> GetAsync(string key, Func<Task<T>> factory, MemoryCacheEntryOptions options)
    {
        var cacheKey = $"{_prefix}:{key}";
        return await _cache.GetOrSetAsync(cacheKey, factory, options);
    }

    public void InvalidateAsync(string key)
    {
        var cacheKey = $"{_prefix}:{key}";
        _cache.Remove(cacheKey);
    }

    public void InvalidateAllAsync()
    {
        _cache.RemoveByPattern($"^{_prefix}:");
    }

    public void InvalidateByPatternAsync(string pattern)
    {
        var fullPattern = $"^{_prefix}:{pattern}";
        _cache.RemoveByPattern(fullPattern);
    }
}
```

## 2. Distributed Caching with Redis

### Redis Distributed Cache Implementation
```csharp
public interface IDistributedCacheService
{
    Task<T> GetAsync<T>(string key) where T : class;
    Task SetAsync<T>(string key, T value, TimeSpan expiration) where T : class;
    Task SetAsync<T>(string key, T value, DistributedCacheEntryOptions options) where T : class;
    Task RemoveAsync(string key);
    Task RemoveByPatternAsync(string pattern);
    Task<bool> ExistsAsync(string key);
}

public class RedisDistributedCacheService : IDistributedCacheService
{
    private readonly IDistributedCache _distributedCache;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisDistributedCacheService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public RedisDistributedCacheService(
        IDistributedCache distributedCache,
        IConnectionMultiplexer redis,
        ILogger<RedisDistributedCacheService> logger)
    {
        _distributedCache = distributedCache;
        _redis = redis;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    public async Task<T> GetAsync<T>(string key) where T : class
    {
        try
        {
            var cached = await _distributedCache.GetStringAsync(key);
            
            if (cached == null)
            {
                _logger.LogDebug("Cache miss for key: {Key}", key);
                return null;
            }

            _logger.LogDebug("Cache hit for key: {Key}", key);
            return JsonSerializer.Deserialize<T>(cached, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving cache for key: {Key}", key);
            return null;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan expiration) where T : class
    {
        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = expiration
        };

        await SetAsync(key, value, options);
    }

    public async Task SetAsync<T>(string key, T value, DistributedCacheEntryOptions options) where T : class
    {
        try
        {
            var serialized = JsonSerializer.Serialize(value, _jsonOptions);
            await _distributedCache.SetStringAsync(key, serialized, options);
            
            _logger.LogDebug("Cached value for key: {Key}", key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting cache for key: {Key}", key);
        }
    }

    public async Task RemoveAsync(string key)
    {
        try
        {
            await _distributedCache.RemoveAsync(key);
            _logger.LogDebug("Removed cache entry for key: {Key}", key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing cache for key: {Key}", key);
        }
    }

    public async Task RemoveByPatternAsync(string pattern)
    {
        try
        {
            var database = _redis.GetDatabase();
            var server = _redis.GetServer(_redis.GetEndPoints().First());
            
            var keys = server.Keys(pattern: pattern);
            
            var tasks = keys.Select(key => database.KeyDeleteAsync(key));
            await Task.WhenAll(tasks);
            
            _logger.LogDebug("Removed cache entries matching pattern: {Pattern}", pattern);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing cache by pattern: {Pattern}", pattern);
        }
    }

    public async Task<bool> ExistsAsync(string key)
    {
        try
        {
            var database = _redis.GetDatabase();
            return await database.KeyExistsAsync(key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking cache existence for key: {Key}", key);
            return false;
        }
    }
}
```

### Hybrid Caching Strategy
```csharp
public class HybridCacheService : IHybridCacheService
{
    private readonly IAdvancedMemoryCache _l1Cache;
    private readonly IDistributedCacheService _l2Cache;
    private readonly ILogger<HybridCacheService> _logger;
    private readonly HybridCacheOptions _options;

    public HybridCacheService(
        IAdvancedMemoryCache l1Cache,
        IDistributedCacheService l2Cache,
        IOptions<HybridCacheOptions> options,
        ILogger<HybridCacheService> logger)
    {
        _l1Cache = l1Cache;
        _l2Cache = l2Cache;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan? cacheDuration = null) where T : class
    {
        var duration = cacheDuration ?? _options.DefaultCacheDuration;
        
        // Try L1 cache first
        var l1Result = await _l1Cache.GetOrSetAsync(key, async () =>
        {
            // Try L2 cache if L1 miss
            var l2Result = await _l2Cache.GetAsync<T>(key);
            
            if (l2Result != null)
            {
                _logger.LogDebug("L2 cache hit for key: {Key}", key);
                return l2Result;
            }

            // Both caches miss, get from source
            _logger.LogDebug("Both caches miss for key: {Key}", key);
            var value = await factory();
            
            if (value != null)
            {
                // Store in L2 cache
                await _l2Cache.SetAsync(key, value, duration);
            }
            
            return value;
        }, TimeSpan.FromMinutes(_options.L1CacheDurationMinutes));

        return l1Result;
    }

    public async Task RemoveAsync(string key)
    {
        _l1Cache.Remove(key);
        await _l2Cache.RemoveAsync(key);
    }

    public async Task RemoveByPatternAsync(string pattern)
    {
        _l1Cache.RemoveByPattern(pattern);
        await _l2Cache.RemoveByPatternAsync(pattern);
    }
}

public class HybridCacheOptions
{
    public TimeSpan DefaultCacheDuration { get; set; } = TimeSpan.FromMinutes(30);
    public int L1CacheDurationMinutes { get; set; } = 5;
}
```

## 3. Response Caching

### Advanced Response Caching
```csharp
public class AdvancedResponseCacheAttribute : Attribute, IActionFilter
{
    private readonly int _duration;
    private readonly string _varyByHeader;
    private readonly string _varyByQueryKeys;
    private readonly string _cacheProfileName;

    public AdvancedResponseCacheAttribute(int duration, string varyByHeader = null, string varyByQueryKeys = null, string cacheProfileName = null)
    {
        _duration = duration;
        _varyByHeader = varyByHeader;
        _varyByQueryKeys = varyByQueryKeys;
        _cacheProfileName = cacheProfileName;
    }

    public void OnActionExecuting(ActionExecutingContext context)
    {
        var cacheKey = GenerateCacheKey(context);
        var cachedResponse = GetCachedResponse(cacheKey);

        if (cachedResponse != null)
        {
            context.Result = cachedResponse;
            return;
        }

        context.HttpContext.Items["CacheKey"] = cacheKey;
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
        if (context.Result is ObjectResult objectResult && objectResult.StatusCode == 200)
        {
            var cacheKey = context.HttpContext.Items["CacheKey"] as string;
            if (cacheKey != null)
            {
                CacheResponse(cacheKey, objectResult, _duration);
            }
        }
    }

    private string GenerateCacheKey(ActionExecutingContext context)
    {
        var keyBuilder = new StringBuilder();
        keyBuilder.Append(context.HttpContext.Request.Path);

        if (!string.IsNullOrEmpty(_varyByQueryKeys))
        {
            var queryKeys = _varyByQueryKeys.Split(',');
            foreach (var queryKey in queryKeys)
            {
                if (context.HttpContext.Request.Query.TryGetValue(queryKey.Trim(), out var value))
                {
                    keyBuilder.Append($"_{queryKey}:{value}");
                }
            }
        }

        if (!string.IsNullOrEmpty(_varyByHeader))
        {
            var headerKeys = _varyByHeader.Split(',');
            foreach (var headerKey in headerKeys)
            {
                if (context.HttpContext.Request.Headers.TryGetValue(headerKey.Trim(), out var value))
                {
                    keyBuilder.Append($"_{headerKey}:{value}");
                }
            }
        }

        return keyBuilder.ToString();
    }

    private ObjectResult GetCachedResponse(string cacheKey)
    {
        // Implementation depends on your caching strategy
        // This is a simplified example
        return null;
    }

    private void CacheResponse(string cacheKey, ObjectResult result, int duration)
    {
        // Implementation depends on your caching strategy
        // This is a simplified example
    }
}
```

### ETags and Conditional Requests
```csharp
public class ETagMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ETagMiddleware> _logger;

    public ETagMiddleware(RequestDelegate next, ILogger<ETagMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var originalStream = context.Response.Body;
        
        using var responseStream = new MemoryStream();
        context.Response.Body = responseStream;

        await _next(context);

        if (context.Response.StatusCode == 200 && 
            context.Request.Method == "GET" && 
            responseStream.Length > 0)
        {
            var etag = GenerateETag(responseStream.ToArray());
            context.Response.Headers["ETag"] = etag;

            if (context.Request.Headers.TryGetValue("If-None-Match", out var ifNoneMatch))
            {
                if (ifNoneMatch == etag)
                {
                    context.Response.StatusCode = 304;
                    context.Response.Body = originalStream;
                    return;
                }
            }
        }

        responseStream.Seek(0, SeekOrigin.Begin);
        await responseStream.CopyToAsync(originalStream);
        context.Response.Body = originalStream;
    }

    private string GenerateETag(byte[] data)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(data);
        return $"\"{Convert.ToBase64String(hash)}\"";
    }
}
```

## 4. Performance Monitoring & Profiling

### Application Performance Monitoring
```csharp
public class PerformanceMonitoringMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<PerformanceMonitoringMiddleware> _logger;
    private readonly IMetrics _metrics;
    private readonly PerformanceMonitoringOptions _options;

    public PerformanceMonitoringMiddleware(
        RequestDelegate next,
        ILogger<PerformanceMonitoringMiddleware> logger,
        IMetrics metrics,
        IOptions<PerformanceMonitoringOptions> options)
    {
        _next = next;
        _logger = logger;
        _metrics = metrics;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var startTime = DateTime.UtcNow;

        try
        {
            await _next(context);
        }
        finally
        {
            stopwatch.Stop();
            var duration = stopwatch.ElapsedMilliseconds;

            // Log slow requests
            if (duration > _options.SlowRequestThreshold)
            {
                _logger.LogWarning("Slow request detected: {Method} {Path} took {Duration}ms",
                    context.Request.Method,
                    context.Request.Path,
                    duration);
            }

            // Record metrics
            _metrics.Record("request_duration", duration, new[]
            {
                new KeyValuePair<string, object>("method", context.Request.Method),
                new KeyValuePair<string, object>("path", context.Request.Path.Value),
                new KeyValuePair<string, object>("status_code", context.Response.StatusCode)
            });

            // Memory usage
            var memoryUsage = GC.GetTotalMemory(false);
            _metrics.Record("memory_usage", memoryUsage);

            // Add performance headers
            if (_options.IncludePerformanceHeaders)
            {
                context.Response.Headers["X-Response-Time"] = $"{duration}ms";
                context.Response.Headers["X-Memory-Usage"] = $"{memoryUsage / 1024 / 1024}MB";
            }
        }
    }
}

public class PerformanceMonitoringOptions
{
    public int SlowRequestThreshold { get; set; } = 1000;
    public bool IncludePerformanceHeaders { get; set; } = false;
}
```

### Custom Performance Counters
```csharp
public interface IPerformanceCounterService
{
    void IncrementCounter(string counterName);
    void DecrementCounter(string counterName);
    void SetGauge(string gaugeName, double value);
    void RecordHistogram(string histogramName, double value);
}

public class PerformanceCounterService : IPerformanceCounterService
{
    private readonly ConcurrentDictionary<string, long> _counters = new();
    private readonly ConcurrentDictionary<string, double> _gauges = new();
    private readonly ConcurrentDictionary<string, List<double>> _histograms = new();
    private readonly ILogger<PerformanceCounterService> _logger;

    public PerformanceCounterService(ILogger<PerformanceCounterService> logger)
    {
        _logger = logger;
    }

    public void IncrementCounter(string counterName)
    {
        _counters.AddOrUpdate(counterName, 1, (key, value) => value + 1);
        _logger.LogDebug("Incremented counter {CounterName}", counterName);
    }

    public void DecrementCounter(string counterName)
    {
        _counters.AddOrUpdate(counterName, -1, (key, value) => value - 1);
        _logger.LogDebug("Decremented counter {CounterName}", counterName);
    }

    public void SetGauge(string gaugeName, double value)
    {
        _gauges.AddOrUpdate(gaugeName, value, (key, oldValue) => value);
        _logger.LogDebug("Set gauge {GaugeName} to {Value}", gaugeName, value);
    }

    public void RecordHistogram(string histogramName, double value)
    {
        _histograms.AddOrUpdate(histogramName, new List<double> { value }, (key, list) =>
        {
            list.Add(value);
            return list;
        });
        _logger.LogDebug("Recorded histogram {HistogramName} value {Value}", histogramName, value);
    }

    public Dictionary<string, object> GetMetrics()
    {
        var metrics = new Dictionary<string, object>();

        foreach (var counter in _counters)
        {
            metrics[$"counter_{counter.Key}"] = counter.Value;
        }

        foreach (var gauge in _gauges)
        {
            metrics[$"gauge_{gauge.Key}"] = gauge.Value;
        }

        foreach (var histogram in _histograms)
        {
            var values = histogram.Value;
            metrics[$"histogram_{histogram.Key}_count"] = values.Count;
            metrics[$"histogram_{histogram.Key}_avg"] = values.Average();
            metrics[$"histogram_{histogram.Key}_min"] = values.Min();
            metrics[$"histogram_{histogram.Key}_max"] = values.Max();
        }

        return metrics;
    }
}
```

## 5. Optimization Techniques

### Database Query Optimization
```csharp
public class OptimizedRepository<T> : IRepository<T> where T : class
{
    private readonly DbContext _context;
    private readonly IAdvancedMemoryCache _cache;
    private readonly ILogger<OptimizedRepository<T>> _logger;
    private readonly DbSet<T> _dbSet;

    public OptimizedRepository(DbContext context, IAdvancedMemoryCache cache, ILogger<OptimizedRepository<T>> logger)
    {
        _context = context;
        _cache = cache;
        _logger = logger;
        _dbSet = context.Set<T>();
    }

    public async Task<T> GetByIdAsync(int id)
    {
        var cacheKey = $"{typeof(T).Name}_{id}";
        
        return await _cache.GetOrSetAsync(cacheKey, async () =>
        {
            var entity = await _dbSet.FindAsync(id);
            
            if (entity != null)
            {
                _context.Entry(entity).State = EntityState.Detached;
            }
            
            return entity;
        }, TimeSpan.FromMinutes(15));
    }

    public async Task<IEnumerable<T>> GetPagedAsync(int page, int pageSize, Expression<Func<T, bool>> predicate = null)
    {
        var query = _dbSet.AsNoTracking();

        if (predicate != null)
        {
            query = query.Where(predicate);
        }

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return items;
    }

    public async Task<T> AddAsync(T entity)
    {
        _dbSet.Add(entity);
        await _context.SaveChangesAsync();
        
        // Invalidate related cache entries
        InvalidateCache(entity);
        
        return entity;
    }

    public async Task UpdateAsync(T entity)
    {
        _context.Entry(entity).State = EntityState.Modified;
        await _context.SaveChangesAsync();
        
        // Invalidate related cache entries
        InvalidateCache(entity);
    }

    public async Task DeleteAsync(int id)
    {
        var entity = await _dbSet.FindAsync(id);
        if (entity != null)
        {
            _dbSet.Remove(entity);
            await _context.SaveChangesAsync();
            
            // Invalidate related cache entries
            InvalidateCache(entity);
        }
    }

    private void InvalidateCache(T entity)
    {
        var entityType = typeof(T).Name;
        var pattern = $"^{entityType}_";
        _cache.RemoveByPattern(pattern);
        
        _logger.LogDebug("Invalidated cache for entity type: {EntityType}", entityType);
    }
}
```

### Async Best Practices
```csharp
public class AsyncBestPracticesService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AsyncBestPracticesService> _logger;
    private readonly SemaphoreSlim _semaphore;

    public AsyncBestPracticesService(HttpClient httpClient, ILogger<AsyncBestPracticesService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _semaphore = new SemaphoreSlim(10, 10); // Limit concurrent requests
    }

    // Good: Async all the way
    public async Task<IEnumerable<T>> ProcessItemsAsync<T>(IEnumerable<string> urls, Func<string, Task<T>> processor)
    {
        var tasks = urls.Select(async url =>
        {
            await _semaphore.WaitAsync();
            try
            {
                return await processor(url);
            }
            finally
            {
                _semaphore.Release();
            }
        });

        return await Task.WhenAll(tasks);
    }

    // Good: Use ConfigureAwait(false) in library code
    public async Task<string> GetDataAsync(string url)
    {
        try
        {
            var response = await _httpClient.GetAsync(url).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Error fetching data from {Url}", url);
            throw;
        }
    }

    // Good: Use cancellation tokens
    public async Task<T> ProcessWithTimeoutAsync<T>(Func<CancellationToken, Task<T>> operation, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        
        try
        {
            return await operation(cts.Token);
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
        {
            throw new TimeoutException($"Operation timed out after {timeout}");
        }
    }

    // Good: Batch operations
    public async Task<IEnumerable<T>> BatchProcessAsync<T>(IEnumerable<string> items, Func<IEnumerable<string>, Task<IEnumerable<T>>> batchProcessor, int batchSize = 10)
    {
        var results = new List<T>();
        var batches = items.Chunk(batchSize);

        foreach (var batch in batches)
        {
            var batchResults = await batchProcessor(batch);
            results.AddRange(batchResults);
        }

        return results;
    }
}
```

## 6. Configuration and Registration

### Complete Performance Setup
```csharp
public void ConfigureServices(IServiceCollection services)
{
    // Memory caching
    services.AddMemoryCache();
    services.AddSingleton<IAdvancedMemoryCache, AdvancedMemoryCache>();
    
    // Distributed caching (Redis)
    services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = Configuration.GetConnectionString("Redis");
        options.InstanceName = "MyApp";
    });
    
    // Redis connection
    services.AddSingleton<IConnectionMultiplexer>(provider =>
    {
        var configuration = provider.GetRequiredService<IConfiguration>();
        var connectionString = configuration.GetConnectionString("Redis");
        return ConnectionMultiplexer.Connect(connectionString);
    });
    
    // Cache services
    services.AddScoped<IDistributedCacheService, RedisDistributedCacheService>();
    services.AddScoped(typeof(CacheAsideService<>));
    
    // Hybrid caching
    services.Configure<HybridCacheOptions>(Configuration.GetSection("HybridCache"));
    services.AddScoped<IHybridCacheService, HybridCacheService>();
    
    // Performance monitoring
    services.Configure<PerformanceMonitoringOptions>(Configuration.GetSection("PerformanceMonitoring"));
    services.AddSingleton<IPerformanceCounterService, PerformanceCounterService>();
    
    // Response caching
    services.AddResponseCaching(options =>
    {
        options.UseCaseSensitivePaths = true;
        options.MaximumBodySize = 1024 * 1024; // 1MB
    });
    
    // HTTP client with optimizations
    services.AddHttpClient<AsyncBestPracticesService>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
    }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        MaxConnectionsPerServer = 100
    });
}

public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
{
    // Performance monitoring
    app.UseMiddleware<PerformanceMonitoringMiddleware>();
    
    // Response caching
    app.UseResponseCaching();
    
    // ETags
    app.UseMiddleware<ETagMiddleware>();
    
    // Compression
    app.UseResponseCompression();
    
    app.UseRouting();
    app.UseEndpoints(endpoints =>
    {
        endpoints.MapControllers();
        
        // Performance metrics endpoint
        endpoints.MapGet("/metrics", async context =>
        {
            var performanceService = context.RequestServices.GetRequiredService<IPerformanceCounterService>();
            var metrics = ((PerformanceCounterService)performanceService).GetMetrics();
            await context.Response.WriteAsync(JsonSerializer.Serialize(metrics));
        });
    });
}
```

## 7. Usage Examples

### Controller with Advanced Caching
```csharp
[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    private readonly IHybridCacheService _cache;
    private readonly IProductService _productService;
    private readonly IPerformanceCounterService _performanceCounters;

    public ProductsController(
        IHybridCacheService cache,
        IProductService productService,
        IPerformanceCounterService performanceCounters)
    {
        _cache = cache;
        _productService = productService;
        _performanceCounters = performanceCounters;
    }

    [HttpGet("{id}")]
    [AdvancedResponseCache(300, varyByQueryKeys: "includeDetails")]
    public async Task<ActionResult<Product>> GetProduct(int id, bool includeDetails = false)
    {
        _performanceCounters.IncrementCounter("products_get_requests");
        
        var cacheKey = $"product_{id}_{includeDetails}";
        
        var product = await _cache.GetOrSetAsync(cacheKey, async () =>
        {
            _performanceCounters.IncrementCounter("products_cache_miss");
            return await _productService.GetByIdAsync(id, includeDetails);
        }, TimeSpan.FromMinutes(30));

        if (product == null)
        {
            return NotFound();
        }

        return Ok(product);
    }

    [HttpPost]
    public async Task<ActionResult<Product>> CreateProduct([FromBody] CreateProductRequest request)
    {
        _performanceCounters.IncrementCounter("products_create_requests");
        
        var product = await _productService.CreateAsync(request);
        
        // Invalidate related caches
        await _cache.RemoveByPatternAsync("product_*");
        await _cache.RemoveByPatternAsync("products_list_*");
        
        return CreatedAtAction(nameof(GetProduct), new { id = product.Id }, product);
    }
}
```