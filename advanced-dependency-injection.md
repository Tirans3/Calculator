# Advanced Dependency Injection in ASP.NET Core

## 1. Factory Pattern with DI

### Abstract Factory Pattern
```csharp
public interface IProcessorFactory
{
    IDataProcessor CreateProcessor(string type);
}

public class ProcessorFactory : IProcessorFactory
{
    private readonly IServiceProvider _serviceProvider;
    
    public ProcessorFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public IDataProcessor CreateProcessor(string type)
    {
        return type.ToLowerInvariant() switch
        {
            "json" => _serviceProvider.GetRequiredService<JsonProcessor>(),
            "xml" => _serviceProvider.GetRequiredService<XmlProcessor>(),
            "csv" => _serviceProvider.GetRequiredService<CsvProcessor>(),
            _ => _serviceProvider.GetRequiredService<DefaultProcessor>()
        };
    }
}

// Usage in controller
[ApiController]
[Route("[controller]")]
public class DataController : ControllerBase
{
    private readonly IProcessorFactory _processorFactory;
    
    public DataController(IProcessorFactory processorFactory)
    {
        _processorFactory = processorFactory;
    }

    [HttpPost("process/{type}")]
    public async Task<IActionResult> ProcessData(string type, [FromBody] string data)
    {
        var processor = _processorFactory.CreateProcessor(type);
        var result = await processor.ProcessAsync(data);
        return Ok(result);
    }
}
```

### Generic Factory Pattern
```csharp
public interface IGenericFactory<T>
{
    T Create<TImpl>() where TImpl : class, T;
    T Create(Type implementationType);
}

public class GenericFactory<T> : IGenericFactory<T>
{
    private readonly IServiceProvider _serviceProvider;
    
    public GenericFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public T Create<TImpl>() where TImpl : class, T
    {
        return _serviceProvider.GetRequiredService<TImpl>();
    }

    public T Create(Type implementationType)
    {
        return (T)_serviceProvider.GetRequiredService(implementationType);
    }
}
```

## 2. Advanced Service Registration

### Conditional Registration
```csharp
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddConditionalService<TService, TImplementation>(
        this IServiceCollection services,
        Func<IServiceProvider, bool> condition)
        where TService : class
        where TImplementation : class, TService
    {
        services.AddScoped<TService>(provider =>
        {
            if (condition(provider))
            {
                return provider.GetRequiredService<TImplementation>();
            }
            
            throw new InvalidOperationException("Condition not met for service registration");
        });
        
        services.AddScoped<TImplementation>();
        return services;
    }

    public static IServiceCollection AddServiceWithFallback<TService, TPrimary, TFallback>(
        this IServiceCollection services)
        where TService : class
        where TPrimary : class, TService
        where TFallback : class, TService
    {
        services.AddScoped<TPrimary>();
        services.AddScoped<TFallback>();
        
        services.AddScoped<TService>(provider =>
        {
            try
            {
                return provider.GetRequiredService<TPrimary>();
            }
            catch
            {
                return provider.GetRequiredService<TFallback>();
            }
        });

        return services;
    }
}
```

### Dynamic Service Registration
```csharp
public static class DynamicServiceRegistration
{
    public static IServiceCollection AddServicesFromAssembly(
        this IServiceCollection services,
        Assembly assembly,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
    {
        var serviceTypes = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Name.EndsWith("Service"))
            .ToList();

        foreach (var serviceType in serviceTypes)
        {
            var interfaceType = serviceType.GetInterfaces()
                .FirstOrDefault(i => i.Name == $"I{serviceType.Name}");

            if (interfaceType != null)
            {
                services.Add(new ServiceDescriptor(interfaceType, serviceType, lifetime));
            }
        }

        return services;
    }

    public static IServiceCollection AddAttributeBasedServices(
        this IServiceCollection services,
        Assembly assembly)
    {
        var typesWithAttribute = assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<ServiceAttribute>() != null)
            .ToList();

        foreach (var type in typesWithAttribute)
        {
            var attribute = type.GetCustomAttribute<ServiceAttribute>();
            var serviceType = attribute.ServiceType ?? type;
            
            services.Add(new ServiceDescriptor(serviceType, type, attribute.Lifetime));
        }

        return services;
    }
}

[AttributeUsage(AttributeTargets.Class)]
public class ServiceAttribute : Attribute
{
    public Type ServiceType { get; set; }
    public ServiceLifetime Lifetime { get; set; } = ServiceLifetime.Scoped;
}

// Usage
[Service(ServiceType = typeof(IEmailService), Lifetime = ServiceLifetime.Singleton)]
public class EmailService : IEmailService
{
    // Implementation
}
```

## 3. Decorator Pattern with DI

### Service Decorator
```csharp
public interface IUserService
{
    Task<User> GetUserAsync(int id);
    Task<User> CreateUserAsync(User user);
}

public class UserService : IUserService
{
    private readonly IUserRepository _repository;
    
    public UserService(IUserRepository repository)
    {
        _repository = repository;
    }

    public async Task<User> GetUserAsync(int id)
    {
        return await _repository.GetByIdAsync(id);
    }

    public async Task<User> CreateUserAsync(User user)
    {
        return await _repository.CreateAsync(user);
    }
}

// Caching decorator
public class CachedUserService : IUserService
{
    private readonly IUserService _userService;
    private readonly IMemoryCache _cache;
    
    public CachedUserService(IUserService userService, IMemoryCache cache)
    {
        _userService = userService;
        _cache = cache;
    }

    public async Task<User> GetUserAsync(int id)
    {
        var cacheKey = $"user_{id}";
        
        if (_cache.TryGetValue(cacheKey, out User cachedUser))
        {
            return cachedUser;
        }

        var user = await _userService.GetUserAsync(id);
        
        if (user != null)
        {
            _cache.Set(cacheKey, user, TimeSpan.FromMinutes(10));
        }

        return user;
    }

    public async Task<User> CreateUserAsync(User user)
    {
        var result = await _userService.CreateUserAsync(user);
        
        // Invalidate cache
        _cache.Remove($"user_{result.Id}");
        
        return result;
    }
}

// Logging decorator
public class LoggingUserService : IUserService
{
    private readonly IUserService _userService;
    private readonly ILogger<LoggingUserService> _logger;
    
    public LoggingUserService(IUserService userService, ILogger<LoggingUserService> logger)
    {
        _userService = userService;
        _logger = logger;
    }

    public async Task<User> GetUserAsync(int id)
    {
        _logger.LogInformation("Getting user with ID: {UserId}", id);
        
        try
        {
            var user = await _userService.GetUserAsync(id);
            _logger.LogInformation("Successfully retrieved user: {UserId}", id);
            return user;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting user with ID: {UserId}", id);
            throw;
        }
    }

    public async Task<User> CreateUserAsync(User user)
    {
        _logger.LogInformation("Creating user: {UserEmail}", user.Email);
        
        try
        {
            var result = await _userService.CreateUserAsync(user);
            _logger.LogInformation("Successfully created user: {UserId}", result.Id);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating user: {UserEmail}", user.Email);
            throw;
        }
    }
}
```

## 4. Advanced Service Lifetimes

### Custom Service Scope
```csharp
public class CustomScopeService : IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private IServiceScope _scope;
    
    public CustomScopeService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public T GetService<T>()
    {
        _scope ??= _serviceProvider.CreateScope();
        return _scope.ServiceProvider.GetRequiredService<T>();
    }

    public void Dispose()
    {
        _scope?.Dispose();
    }
}

// Per-tenant service scope
public class TenantScopedService<T> where T : class
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, T> _tenantServices = new();
    
    public TenantScopedService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public T GetService(string tenantId)
    {
        return _tenantServices.GetOrAdd(tenantId, _ => 
            _serviceProvider.GetRequiredService<T>());
    }
}
```

## 5. Registration Examples

### Startup Configuration
```csharp
public void ConfigureServices(IServiceCollection services)
{
    // Basic registration
    services.AddScoped<IUserRepository, UserRepository>();
    
    // Factory pattern
    services.AddScoped<IProcessorFactory, ProcessorFactory>();
    services.AddScoped<JsonProcessor>();
    services.AddScoped<XmlProcessor>();
    services.AddScoped<CsvProcessor>();
    services.AddScoped<DefaultProcessor>();
    
    // Generic factory
    services.AddScoped<IGenericFactory<IDataProcessor>, GenericFactory<IDataProcessor>>();
    
    // Decorator pattern registration
    services.AddScoped<UserService>();
    services.AddScoped<IUserService>(provider =>
    {
        var userService = provider.GetRequiredService<UserService>();
        var cachedService = new CachedUserService(userService, provider.GetRequiredService<IMemoryCache>());
        return new LoggingUserService(cachedService, provider.GetRequiredService<ILogger<LoggingUserService>>());
    });
    
    // Conditional registration
    services.AddConditionalService<IPaymentService, StripePaymentService>(
        provider => provider.GetRequiredService<IConfiguration>()["PaymentProvider"] == "Stripe");
    
    // Service with fallback
    services.AddServiceWithFallback<IEmailService, SmtpEmailService, ConsoleEmailService>();
    
    // Dynamic registration
    services.AddServicesFromAssembly(Assembly.GetExecutingAssembly());
    services.AddAttributeBasedServices(Assembly.GetExecutingAssembly());
    
    // Custom scope
    services.AddScoped<CustomScopeService>();
    services.AddSingleton(typeof(TenantScopedService<>));
}
```

### Configuration-based Registration
```csharp
public class ServiceConfiguration
{
    public string ServiceType { get; set; }
    public string Implementation { get; set; }
    public ServiceLifetime Lifetime { get; set; }
    public Dictionary<string, object> Parameters { get; set; }
}

public static class ConfigurationBasedRegistration
{
    public static IServiceCollection AddConfigurationBasedServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var serviceConfigs = configuration.GetSection("Services")
            .Get<ServiceConfiguration[]>();

        foreach (var config in serviceConfigs)
        {
            var serviceType = Type.GetType(config.ServiceType);
            var implementationType = Type.GetType(config.Implementation);
            
            if (serviceType != null && implementationType != null)
            {
                services.Add(new ServiceDescriptor(serviceType, implementationType, config.Lifetime));
            }
        }

        return services;
    }
}
```

## 6. Advanced DI Patterns

### Service Locator Pattern (Use with Caution)
```csharp
public interface IServiceLocator
{
    T GetService<T>();
    object GetService(Type serviceType);
    IEnumerable<T> GetServices<T>();
}

public class ServiceLocator : IServiceLocator
{
    private readonly IServiceProvider _serviceProvider;
    
    public ServiceLocator(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public T GetService<T>()
    {
        return _serviceProvider.GetRequiredService<T>();
    }

    public object GetService(Type serviceType)
    {
        return _serviceProvider.GetRequiredService(serviceType);
    }

    public IEnumerable<T> GetServices<T>()
    {
        return _serviceProvider.GetServices<T>();
    }
}
```

### Lazy Service Resolution
```csharp
public class LazyServiceExample
{
    private readonly Lazy<IExpensiveService> _expensiveService;
    
    public LazyServiceExample(Lazy<IExpensiveService> expensiveService)
    {
        _expensiveService = expensiveService;
    }

    public async Task<string> GetDataAsync()
    {
        // Service is only created when accessed
        return await _expensiveService.Value.GetDataAsync();
    }
}

// Registration
services.AddScoped<IExpensiveService, ExpensiveService>();
services.AddScoped<LazyServiceExample>();
```