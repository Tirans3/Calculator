# Advanced Middleware in ASP.NET Core

## 1. Custom Middleware with Configuration

### Basic Custom Middleware
```csharp
public class RequestTimingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestTimingMiddleware> _logger;
    private readonly RequestTimingOptions _options;

    public RequestTimingMiddleware(
        RequestDelegate next, 
        ILogger<RequestTimingMiddleware> logger,
        IOptions<RequestTimingOptions> options)
    {
        _next = next;
        _logger = logger;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        
        try
        {
            await _next(context);
        }
        finally
        {
            stopwatch.Stop();
            
            if (stopwatch.ElapsedMilliseconds > _options.SlowRequestThreshold)
            {
                _logger.LogWarning("Slow request detected: {Method} {Path} took {ElapsedMs}ms",
                    context.Request.Method,
                    context.Request.Path,
                    stopwatch.ElapsedMilliseconds);
            }
        }
    }
}

// Configuration class
public class RequestTimingOptions
{
    public int SlowRequestThreshold { get; set; } = 1000; // milliseconds
    public bool EnableDetailedLogging { get; set; } = true;
}
```

### Advanced Middleware with Conditional Logic
```csharp
public class ConditionalMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ConditionalMiddlewareOptions _options;

    public ConditionalMiddleware(RequestDelegate next, IOptions<ConditionalMiddlewareOptions> options)
    {
        _next = next;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip middleware for certain paths
        if (_options.SkipPaths.Any(path => context.Request.Path.StartsWithSegments(path)))
        {
            await _next(context);
            return;
        }

        // Apply middleware logic only for specific conditions
        if (ShouldApplyMiddleware(context))
        {
            await ApplyMiddlewareLogic(context);
        }

        await _next(context);
    }

    private bool ShouldApplyMiddleware(HttpContext context)
    {
        return context.Request.Headers.ContainsKey("X-Custom-Header") ||
               context.Request.Query.ContainsKey("debug");
    }

    private async Task ApplyMiddlewareLogic(HttpContext context)
    {
        // Custom logic here
        context.Response.Headers.Add("X-Processed-By", "ConditionalMiddleware");
    }
}
```

## 2. Middleware Factory Pattern

### Factory-based Middleware
```csharp
public interface IMiddlewareFactory<T> where T : IMiddleware
{
    T Create(HttpContext context);
}

public class DynamicMiddleware : IMiddleware
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DynamicMiddleware> _logger;

    public DynamicMiddleware(IServiceProvider serviceProvider, ILogger<DynamicMiddleware> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // Resolve services based on request context
        var processor = ResolveProcessor(context);
        
        await processor.ProcessAsync(context);
        await next(context);
    }

    private IRequestProcessor ResolveProcessor(HttpContext context)
    {
        var processorType = context.Request.Headers["X-Processor-Type"].FirstOrDefault();
        
        return processorType switch
        {
            "json" => _serviceProvider.GetRequiredService<JsonRequestProcessor>(),
            "xml" => _serviceProvider.GetRequiredService<XmlRequestProcessor>(),
            _ => _serviceProvider.GetRequiredService<DefaultRequestProcessor>()
        };
    }
}
```

## 3. Middleware with State Management

### Stateful Middleware
```csharp
public class RateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IMemoryCache _cache;
    private readonly RateLimitOptions _options;
    private readonly ILogger<RateLimitingMiddleware> _logger;

    public RateLimitingMiddleware(
        RequestDelegate next,
        IMemoryCache cache,
        IOptions<RateLimitOptions> options,
        ILogger<RateLimitingMiddleware> logger)
    {
        _next = next;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var clientId = GetClientIdentifier(context);
        var key = $"rate_limit_{clientId}";

        if (_cache.TryGetValue(key, out RateLimitInfo rateLimitInfo))
        {
            if (rateLimitInfo.RequestCount >= _options.MaxRequests)
            {
                if (DateTime.UtcNow < rateLimitInfo.WindowStart.AddMinutes(_options.WindowMinutes))
                {
                    context.Response.StatusCode = 429; // Too Many Requests
                    await context.Response.WriteAsync("Rate limit exceeded. Try again later.");
                    return;
                }
                else
                {
                    // Reset window
                    rateLimitInfo = new RateLimitInfo
                    {
                        RequestCount = 1,
                        WindowStart = DateTime.UtcNow
                    };
                }
            }
            else
            {
                rateLimitInfo.RequestCount++;
            }
        }
        else
        {
            rateLimitInfo = new RateLimitInfo
            {
                RequestCount = 1,
                WindowStart = DateTime.UtcNow
            };
        }

        _cache.Set(key, rateLimitInfo, TimeSpan.FromMinutes(_options.WindowMinutes));

        await _next(context);
    }

    private string GetClientIdentifier(HttpContext context)
    {
        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}

public class RateLimitInfo
{
    public int RequestCount { get; set; }
    public DateTime WindowStart { get; set; }
}

public class RateLimitOptions
{
    public int MaxRequests { get; set; } = 100;
    public int WindowMinutes { get; set; } = 1;
}
```

## 4. Middleware Extension Methods

### Fluent Extension Methods
```csharp
public static class MiddlewareExtensions
{
    public static IApplicationBuilder UseRequestTiming(
        this IApplicationBuilder app,
        Action<RequestTimingOptions> configureOptions = null)
    {
        var options = new RequestTimingOptions();
        configureOptions?.Invoke(options);

        return app.UseMiddleware<RequestTimingMiddleware>(Options.Create(options));
    }

    public static IApplicationBuilder UseConditionalMiddleware(
        this IApplicationBuilder app,
        Func<HttpContext, bool> condition,
        Action<IApplicationBuilder> configureApp)
    {
        return app.UseWhen(condition, configureApp);
    }

    public static IApplicationBuilder UseRateLimiting(
        this IApplicationBuilder app,
        Action<RateLimitOptions> configureOptions = null)
    {
        var options = new RateLimitOptions();
        configureOptions?.Invoke(options);

        return app.UseMiddleware<RateLimitingMiddleware>(Options.Create(options));
    }
}
```

## 5. Advanced Pipeline Branching

### Pipeline Branching with MapWhen
```csharp
public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
{
    // Branch for API requests
    app.MapWhen(context => context.Request.Path.StartsWithSegments("/api"), apiApp =>
    {
        apiApp.UseMiddleware<ApiAuthenticationMiddleware>();
        apiApp.UseMiddleware<ApiRateLimitingMiddleware>();
        apiApp.UseRouting();
        apiApp.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
        });
    });

    // Branch for webhook requests
    app.MapWhen(context => context.Request.Path.StartsWithSegments("/webhooks"), webhookApp =>
    {
        webhookApp.UseMiddleware<WebhookValidationMiddleware>();
        webhookApp.UseMiddleware<WebhookProcessingMiddleware>();
    });

    // Default pipeline
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseEndpoints(endpoints =>
    {
        endpoints.MapRazorPages();
    });
}
```

## 6. Registration in Startup
```csharp
public void ConfigureServices(IServiceCollection services)
{
    // Register middleware dependencies
    services.Configure<RequestTimingOptions>(Configuration.GetSection("RequestTiming"));
    services.Configure<RateLimitOptions>(Configuration.GetSection("RateLimit"));
    
    // Register as scoped for factory pattern
    services.AddScoped<DynamicMiddleware>();
    
    // Register processors
    services.AddScoped<JsonRequestProcessor>();
    services.AddScoped<XmlRequestProcessor>();
    services.AddScoped<DefaultRequestProcessor>();
}

public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
{
    // Use custom middleware with configuration
    app.UseRequestTiming(options =>
    {
        options.SlowRequestThreshold = 500;
        options.EnableDetailedLogging = true;
    });

    app.UseRateLimiting(options =>
    {
        options.MaxRequests = 50;
        options.WindowMinutes = 1;
    });

    app.UseMiddleware<DynamicMiddleware>();
    
    // Standard middleware
    app.UseRouting();
    app.UseEndpoints(endpoints =>
    {
        endpoints.MapControllers();
    });
}
```