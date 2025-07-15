# Advanced Architecture Patterns & Best Practices

## 1. CQRS (Command Query Responsibility Segregation)

### CQRS Implementation with MediatR
```csharp
// Commands
public class CreateProductCommand : IRequest<ProductDto>
{
    public string Name { get; set; }
    public decimal Price { get; set; }
    public string Description { get; set; }
}

public class CreateProductCommandHandler : IRequestHandler<CreateProductCommand, ProductDto>
{
    private readonly IProductRepository _repository;
    private readonly IMapper _mapper;
    private readonly ILogger<CreateProductCommandHandler> _logger;

    public CreateProductCommandHandler(
        IProductRepository repository,
        IMapper mapper,
        ILogger<CreateProductCommandHandler> logger)
    {
        _repository = repository;
        _mapper = mapper;
        _logger = logger;
    }

    public async Task<ProductDto> Handle(CreateProductCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Creating product: {ProductName}", request.Name);

        var product = new Product
        {
            Name = request.Name,
            Price = request.Price,
            Description = request.Description,
            CreatedAt = DateTime.UtcNow
        };

        var createdProduct = await _repository.CreateAsync(product);
        
        // Publish domain event
        await _mediator.Publish(new ProductCreatedEvent(createdProduct.Id), cancellationToken);

        return _mapper.Map<ProductDto>(createdProduct);
    }
}

// Queries
public class GetProductQuery : IRequest<ProductDto>
{
    public int Id { get; set; }
}

public class GetProductQueryHandler : IRequestHandler<GetProductQuery, ProductDto>
{
    private readonly IProductReadRepository _readRepository;
    private readonly IMapper _mapper;
    private readonly IHybridCacheService _cache;

    public GetProductQueryHandler(
        IProductReadRepository readRepository,
        IMapper mapper,
        IHybridCacheService cache)
    {
        _readRepository = readRepository;
        _mapper = mapper;
        _cache = cache;
    }

    public async Task<ProductDto> Handle(GetProductQuery request, CancellationToken cancellationToken)
    {
        var cacheKey = $"product_{request.Id}";
        
        var product = await _cache.GetOrSetAsync(cacheKey, async () =>
            await _readRepository.GetByIdAsync(request.Id), TimeSpan.FromMinutes(15));

        return _mapper.Map<ProductDto>(product);
    }
}

// Domain Events
public class ProductCreatedEvent : INotification
{
    public int ProductId { get; }
    public DateTime CreatedAt { get; }

    public ProductCreatedEvent(int productId)
    {
        ProductId = productId;
        CreatedAt = DateTime.UtcNow;
    }
}

public class ProductCreatedEventHandler : INotificationHandler<ProductCreatedEvent>
{
    private readonly IEmailService _emailService;
    private readonly ILogger<ProductCreatedEventHandler> _logger;

    public ProductCreatedEventHandler(IEmailService emailService, ILogger<ProductCreatedEventHandler> logger)
    {
        _emailService = emailService;
        _logger = logger;
    }

    public async Task Handle(ProductCreatedEvent notification, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Product created event received for product: {ProductId}", notification.ProductId);

        // Send notification email
        await _emailService.SendProductCreatedNotificationAsync(notification.ProductId);
    }
}
```

### CQRS Controller Implementation
```csharp
[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ILogger<ProductsController> _logger;

    public ProductsController(IMediator mediator, ILogger<ProductsController> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ProductDto>> GetProduct(int id)
    {
        try
        {
            var query = new GetProductQuery { Id = id };
            var product = await _mediator.Send(query);
            
            if (product == null)
                return NotFound();

            return Ok(product);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting product {ProductId}", id);
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpPost]
    public async Task<ActionResult<ProductDto>> CreateProduct([FromBody] CreateProductCommand command)
    {
        try
        {
            var product = await _mediator.Send(command);
            return CreatedAtAction(nameof(GetProduct), new { id = product.Id }, product);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating product");
            return StatusCode(500, "Internal server error");
        }
    }
}
```

## 2. Clean Architecture Implementation

### Domain Layer
```csharp
// Domain Entities
public abstract class BaseEntity
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    
    private readonly List<IDomainEvent> _domainEvents = new();
    
    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();
    
    public void AddDomainEvent(IDomainEvent domainEvent)
    {
        _domainEvents.Add(domainEvent);
    }
    
    public void RemoveDomainEvent(IDomainEvent domainEvent)
    {
        _domainEvents.Remove(domainEvent);
    }
    
    public void ClearDomainEvents()
    {
        _domainEvents.Clear();
    }
}

public class Product : BaseEntity
{
    public string Name { get; set; }
    public decimal Price { get; set; }
    public string Description { get; set; }
    public bool IsActive { get; set; }
    
    public void UpdatePrice(decimal newPrice)
    {
        if (newPrice <= 0)
            throw new ArgumentException("Price must be positive", nameof(newPrice));
            
        var oldPrice = Price;
        Price = newPrice;
        
        AddDomainEvent(new ProductPriceChangedEvent(Id, oldPrice, newPrice));
    }
    
    public void Deactivate()
    {
        IsActive = false;
        AddDomainEvent(new ProductDeactivatedEvent(Id));
    }
}

// Domain Events
public interface IDomainEvent
{
    DateTime OccurredOn { get; }
}

public class ProductPriceChangedEvent : IDomainEvent
{
    public int ProductId { get; }
    public decimal OldPrice { get; }
    public decimal NewPrice { get; }
    public DateTime OccurredOn { get; }

    public ProductPriceChangedEvent(int productId, decimal oldPrice, decimal newPrice)
    {
        ProductId = productId;
        OldPrice = oldPrice;
        NewPrice = newPrice;
        OccurredOn = DateTime.UtcNow;
    }
}

// Repository Interfaces
public interface IProductRepository
{
    Task<Product> GetByIdAsync(int id);
    Task<IEnumerable<Product>> GetAllAsync();
    Task<Product> CreateAsync(Product product);
    Task UpdateAsync(Product product);
    Task DeleteAsync(int id);
}

// Domain Services
public interface IPricingService
{
    Task<decimal> CalculateDiscountedPriceAsync(int productId, decimal discountPercentage);
}

public class PricingService : IPricingService
{
    private readonly IProductRepository _productRepository;
    
    public PricingService(IProductRepository productRepository)
    {
        _productRepository = productRepository;
    }
    
    public async Task<decimal> CalculateDiscountedPriceAsync(int productId, decimal discountPercentage)
    {
        var product = await _productRepository.GetByIdAsync(productId);
        if (product == null)
            throw new ArgumentException("Product not found", nameof(productId));
            
        return product.Price * (1 - discountPercentage / 100);
    }
}
```

### Application Layer
```csharp
// Application Services
public interface IProductApplicationService
{
    Task<ProductDto> CreateProductAsync(CreateProductDto dto);
    Task<ProductDto> GetProductAsync(int id);
    Task UpdateProductPriceAsync(int id, decimal newPrice);
}

public class ProductApplicationService : IProductApplicationService
{
    private readonly IProductRepository _productRepository;
    private readonly IPricingService _pricingService;
    private readonly IMapper _mapper;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDomainEventDispatcher _eventDispatcher;

    public ProductApplicationService(
        IProductRepository productRepository,
        IPricingService pricingService,
        IMapper mapper,
        IUnitOfWork unitOfWork,
        IDomainEventDispatcher eventDispatcher)
    {
        _productRepository = productRepository;
        _pricingService = pricingService;
        _mapper = mapper;
        _unitOfWork = unitOfWork;
        _eventDispatcher = eventDispatcher;
    }

    public async Task<ProductDto> CreateProductAsync(CreateProductDto dto)
    {
        var product = new Product
        {
            Name = dto.Name,
            Price = dto.Price,
            Description = dto.Description,
            IsActive = true
        };

        product.AddDomainEvent(new ProductCreatedEvent(product.Id));

        var createdProduct = await _productRepository.CreateAsync(product);
        await _unitOfWork.SaveChangesAsync();

        // Dispatch domain events
        await _eventDispatcher.DispatchAsync(product.DomainEvents);

        return _mapper.Map<ProductDto>(createdProduct);
    }

    public async Task<ProductDto> GetProductAsync(int id)
    {
        var product = await _productRepository.GetByIdAsync(id);
        return _mapper.Map<ProductDto>(product);
    }

    public async Task UpdateProductPriceAsync(int id, decimal newPrice)
    {
        var product = await _productRepository.GetByIdAsync(id);
        if (product == null)
            throw new ArgumentException("Product not found", nameof(id));

        product.UpdatePrice(newPrice);
        await _productRepository.UpdateAsync(product);
        await _unitOfWork.SaveChangesAsync();

        // Dispatch domain events
        await _eventDispatcher.DispatchAsync(product.DomainEvents);
    }
}

// Unit of Work Pattern
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public class UnitOfWork : IUnitOfWork
{
    private readonly ApplicationDbContext _context;

    public UnitOfWork(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return await _context.SaveChangesAsync(cancellationToken);
    }
}
```

### Infrastructure Layer
```csharp
// Repository Implementation
public class ProductRepository : IProductRepository
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<ProductRepository> _logger;

    public ProductRepository(ApplicationDbContext context, ILogger<ProductRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<Product> GetByIdAsync(int id)
    {
        return await _context.Products.FindAsync(id);
    }

    public async Task<IEnumerable<Product>> GetAllAsync()
    {
        return await _context.Products.ToListAsync();
    }

    public async Task<Product> CreateAsync(Product product)
    {
        _context.Products.Add(product);
        return product;
    }

    public async Task UpdateAsync(Product product)
    {
        _context.Entry(product).State = EntityState.Modified;
    }

    public async Task DeleteAsync(int id)
    {
        var product = await GetByIdAsync(id);
        if (product != null)
        {
            _context.Products.Remove(product);
        }
    }
}

// Domain Event Dispatcher
public interface IDomainEventDispatcher
{
    Task DispatchAsync(IEnumerable<IDomainEvent> domainEvents);
}

public class DomainEventDispatcher : IDomainEventDispatcher
{
    private readonly IMediator _mediator;
    private readonly ILogger<DomainEventDispatcher> _logger;

    public DomainEventDispatcher(IMediator mediator, ILogger<DomainEventDispatcher> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    public async Task DispatchAsync(IEnumerable<IDomainEvent> domainEvents)
    {
        foreach (var domainEvent in domainEvents)
        {
            _logger.LogInformation("Dispatching domain event: {EventType}", domainEvent.GetType().Name);
            await _mediator.Publish(domainEvent);
        }
    }
}
```

## 3. Background Services & Hosted Services

### Advanced Background Service Implementation
```csharp
public class EmailProcessingService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<EmailProcessingService> _logger;
    private readonly EmailProcessingOptions _options;

    public EmailProcessingService(
        IServiceProvider serviceProvider,
        ILogger<EmailProcessingService> logger,
        IOptions<EmailProcessingOptions> options)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Email processing service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
                var performanceCounters = scope.ServiceProvider.GetRequiredService<IPerformanceCounterService>();

                await ProcessPendingEmailsAsync(emailService, performanceCounters);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing emails");
            }

            await Task.Delay(_options.ProcessingInterval, stoppingToken);
        }

        _logger.LogInformation("Email processing service stopped");
    }

    private async Task ProcessPendingEmailsAsync(IEmailService emailService, IPerformanceCounterService performanceCounters)
    {
        var pendingEmails = await emailService.GetPendingEmailsAsync(_options.BatchSize);
        
        if (!pendingEmails.Any())
            return;

        _logger.LogInformation("Processing {Count} pending emails", pendingEmails.Count());

        var tasks = pendingEmails.Select(async email =>
        {
            try
            {
                await emailService.SendEmailAsync(email);
                performanceCounters.IncrementCounter("emails_sent");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email {EmailId}", email.Id);
                performanceCounters.IncrementCounter("emails_failed");
            }
        });

        await Task.WhenAll(tasks);
    }
}

public class EmailProcessingOptions
{
    public TimeSpan ProcessingInterval { get; set; } = TimeSpan.FromMinutes(1);
    public int BatchSize { get; set; } = 10;
}
```

### Queue-Based Background Processing
```csharp
public interface IBackgroundTaskQueue
{
    void QueueBackgroundWorkItem(Func<CancellationToken, Task> workItem);
    Task<Func<CancellationToken, Task>> DequeueAsync(CancellationToken cancellationToken);
}

public class BackgroundTaskQueue : IBackgroundTaskQueue
{
    private readonly Channel<Func<CancellationToken, Task>> _queue;
    private readonly ILogger<BackgroundTaskQueue> _logger;

    public BackgroundTaskQueue(int capacity, ILogger<BackgroundTaskQueue> logger)
    {
        _logger = logger;
        
        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        };

        _queue = Channel.CreateBounded<Func<CancellationToken, Task>>(options);
    }

    public void QueueBackgroundWorkItem(Func<CancellationToken, Task> workItem)
    {
        if (workItem == null)
            throw new ArgumentNullException(nameof(workItem));

        if (!_queue.Writer.TryWrite(workItem))
        {
            _logger.LogWarning("Failed to queue background work item - queue is full");
        }
    }

    public async Task<Func<CancellationToken, Task>> DequeueAsync(CancellationToken cancellationToken)
    {
        var workItem = await _queue.Reader.ReadAsync(cancellationToken);
        return workItem;
    }
}

public class QueuedHostedService : BackgroundService
{
    private readonly IBackgroundTaskQueue _taskQueue;
    private readonly ILogger<QueuedHostedService> _logger;

    public QueuedHostedService(IBackgroundTaskQueue taskQueue, ILogger<QueuedHostedService> logger)
    {
        _taskQueue = taskQueue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Queued hosted service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var workItem = await _taskQueue.DequeueAsync(stoppingToken);
                await workItem(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred executing work item");
            }
        }

        _logger.LogInformation("Queued hosted service stopped");
    }
}
```

## 4. Health Checks & Monitoring

### Advanced Health Check Implementation
```csharp
public class DatabaseHealthCheck : IHealthCheck
{
    private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;
    private readonly ILogger<DatabaseHealthCheck> _logger;

    public DatabaseHealthCheck(IDbContextFactory<ApplicationDbContext> contextFactory, ILogger<DatabaseHealthCheck> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var dbContext = await _contextFactory.CreateDbContextAsync(cancellationToken);
            
            var canConnect = await dbContext.Database.CanConnectAsync(cancellationToken);
            
            if (!canConnect)
            {
                return HealthCheckResult.Unhealthy("Cannot connect to database");
            }

            // Check if we can execute a simple query
            var productCount = await dbContext.Products.CountAsync(cancellationToken);
            
            return HealthCheckResult.Healthy($"Database is healthy. Products count: {productCount}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database health check failed");
            return HealthCheckResult.Unhealthy("Database health check failed", ex);
        }
    }
}

public class RedisHealthCheck : IHealthCheck
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisHealthCheck> _logger;

    public RedisHealthCheck(IConnectionMultiplexer redis, ILogger<RedisHealthCheck> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var database = _redis.GetDatabase();
            
            // Test Redis connection with a simple ping
            var pingResult = await database.PingAsync();
            
            if (pingResult.TotalMilliseconds > 100)
            {
                return HealthCheckResult.Degraded($"Redis is slow. Ping time: {pingResult.TotalMilliseconds}ms");
            }

            return HealthCheckResult.Healthy($"Redis is healthy. Ping time: {pingResult.TotalMilliseconds}ms");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis health check failed");
            return HealthCheckResult.Unhealthy("Redis health check failed", ex);
        }
    }
}

public class ExternalApiHealthCheck : IHealthCheck
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ExternalApiHealthCheck> _logger;
    private readonly ExternalApiOptions _options;

    public ExternalApiHealthCheck(HttpClient httpClient, ILogger<ExternalApiHealthCheck> logger, IOptions<ExternalApiOptions> options)
    {
        _httpClient = httpClient;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync(_options.HealthEndpoint, cancellationToken);
            
            if (response.IsSuccessStatusCode)
            {
                return HealthCheckResult.Healthy("External API is healthy");
            }
            else
            {
                return HealthCheckResult.Unhealthy($"External API returned status code: {response.StatusCode}");
            }
        }
        catch (TaskCanceledException)
        {
            return HealthCheckResult.Unhealthy("External API health check timed out");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "External API health check failed");
            return HealthCheckResult.Unhealthy("External API health check failed", ex);
        }
    }
}
```

### Custom Health Check Response Writer
```csharp
public static class HealthCheckResponseWriter
{
    public static async Task WriteResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var response = new
        {
            status = report.Status.ToString(),
            totalDuration = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                duration = entry.Value.Duration.TotalMilliseconds,
                description = entry.Value.Description,
                exception = entry.Value.Exception?.Message,
                data = entry.Value.Data
            })
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(response));
    }
}
```

## 5. Microservices Patterns

### API Gateway Pattern
```csharp
public class ApiGatewayMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ApiGatewayMiddleware> _logger;
    private readonly ApiGatewayOptions _options;

    public ApiGatewayMiddleware(
        RequestDelegate next,
        IServiceProvider serviceProvider,
        ILogger<ApiGatewayMiddleware> logger,
        IOptions<ApiGatewayOptions> options)
    {
        _next = next;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value;
        var route = _options.Routes.FirstOrDefault(r => path.StartsWith(r.Path));

        if (route == null)
        {
            await _next(context);
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var httpClient = httpClientFactory.CreateClient();

        try
        {
            var targetUrl = $"{route.TargetUrl}{path.Substring(route.Path.Length)}";
            if (context.Request.QueryString.HasValue)
            {
                targetUrl += context.Request.QueryString.Value;
            }

            var requestMessage = new HttpRequestMessage(
                new HttpMethod(context.Request.Method),
                targetUrl);

            // Copy headers
            foreach (var header in context.Request.Headers)
            {
                if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                {
                    requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }
            }

            // Copy body for POST/PUT requests
            if (context.Request.ContentLength.HasValue && context.Request.ContentLength > 0)
            {
                var bodyContent = new byte[context.Request.ContentLength.Value];
                await context.Request.Body.ReadAsync(bodyContent, 0, bodyContent.Length);
                requestMessage.Content = new ByteArrayContent(bodyContent);
            }

            var response = await httpClient.SendAsync(requestMessage);

            context.Response.StatusCode = (int)response.StatusCode;

            foreach (var header in response.Headers)
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }

            await response.Content.CopyToAsync(context.Response.Body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error forwarding request to {TargetUrl}", route.TargetUrl);
            context.Response.StatusCode = 502; // Bad Gateway
        }
    }
}

public class ApiGatewayOptions
{
    public List<RouteConfig> Routes { get; set; } = new();
}

public class RouteConfig
{
    public string Path { get; set; }
    public string TargetUrl { get; set; }
}
```

### Circuit Breaker Pattern
```csharp
public class CircuitBreakerService
{
    private readonly CircuitBreakerOptions _options;
    private readonly ILogger<CircuitBreakerService> _logger;
    private CircuitBreakerState _state = CircuitBreakerState.Closed;
    private int _failureCount = 0;
    private DateTime _lastFailureTime = DateTime.MinValue;
    private readonly object _lock = new object();

    public CircuitBreakerService(IOptions<CircuitBreakerOptions> options, ILogger<CircuitBreakerService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<T> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        if (_state == CircuitBreakerState.Open)
        {
            if (DateTime.UtcNow - _lastFailureTime > _options.Timeout)
            {
                _state = CircuitBreakerState.HalfOpen;
                _logger.LogInformation("Circuit breaker moving to half-open state");
            }
            else
            {
                throw new CircuitBreakerOpenException("Circuit breaker is open");
            }
        }

        try
        {
            var result = await operation();
            OnSuccess();
            return result;
        }
        catch (Exception ex)
        {
            OnFailure();
            throw;
        }
    }

    private void OnSuccess()
    {
        lock (_lock)
        {
            _failureCount = 0;
            _state = CircuitBreakerState.Closed;
        }
    }

    private void OnFailure()
    {
        lock (_lock)
        {
            _failureCount++;
            _lastFailureTime = DateTime.UtcNow;

            if (_failureCount >= _options.FailureThreshold)
            {
                _state = CircuitBreakerState.Open;
                _logger.LogWarning("Circuit breaker opened after {FailureCount} failures", _failureCount);
            }
        }
    }
}

public enum CircuitBreakerState
{
    Closed,
    Open,
    HalfOpen
}

public class CircuitBreakerOptions
{
    public int FailureThreshold { get; set; } = 5;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(1);
}

public class CircuitBreakerOpenException : Exception
{
    public CircuitBreakerOpenException(string message) : base(message) { }
}
```

## 6. Production Ready Configuration

### Complete Startup Configuration
```csharp
public class Startup
{
    public IConfiguration Configuration { get; }
    public IWebHostEnvironment Environment { get; }

    public Startup(IConfiguration configuration, IWebHostEnvironment environment)
    {
        Configuration = configuration;
        Environment = environment;
    }

    public void ConfigureServices(IServiceCollection services)
    {
        // Core services
        services.AddControllers();
        services.AddApiVersioning();
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();

        // Database
        services.AddDbContextFactory<ApplicationDbContext>(options =>
            options.UseSqlServer(Configuration.GetConnectionString("DefaultConnection")));

        // Caching
        services.AddMemoryCache();
        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = Configuration.GetConnectionString("Redis");
        });

        // Authentication & Authorization
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = Configuration["Jwt:Issuer"],
                    ValidAudience = Configuration["Jwt:Audience"],
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Configuration["Jwt:Key"]))
                };
            });

        // MediatR for CQRS
        services.AddMediatR(typeof(Startup).Assembly);

        // AutoMapper
        services.AddAutoMapper(typeof(Startup).Assembly);

        // Background Services
        services.AddHostedService<EmailProcessingService>();
        services.AddHostedService<QueuedHostedService>();
        services.AddSingleton<IBackgroundTaskQueue>(provider =>
            new BackgroundTaskQueue(100, provider.GetRequiredService<ILogger<BackgroundTaskQueue>>()));

        // Health Checks
        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database")
            .AddCheck<RedisHealthCheck>("redis")
            .AddCheck<ExternalApiHealthCheck>("external-api");

        // Application Services
        services.AddScoped<IProductApplicationService, ProductApplicationService>();
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<IPricingService, PricingService>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();

        // Circuit Breaker
        services.Configure<CircuitBreakerOptions>(Configuration.GetSection("CircuitBreaker"));
        services.AddScoped<CircuitBreakerService>();

        // Performance Monitoring
        services.AddSingleton<IPerformanceCounterService, PerformanceCounterService>();

        // Logging
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.AddDebug();
            if (Environment.IsProduction())
            {
                builder.AddApplicationInsights();
            }
        });

        // CORS
        services.AddCors(options =>
        {
            options.AddPolicy("AllowAll", builder =>
            {
                builder.AllowAnyOrigin()
                       .AllowAnyMethod()
                       .AllowAnyHeader();
            });
        });

        // Response Compression
        services.AddResponseCompression();

        // HTTP Clients
        services.AddHttpClient();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
            app.UseSwagger();
            app.UseSwaggerUI();
        }
        else
        {
            app.UseExceptionHandler("/Error");
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.UseResponseCompression();
        app.UseCors("AllowAll");

        // Custom middleware
        app.UseMiddleware<PerformanceMonitoringMiddleware>();
        app.UseMiddleware<SecurityHeadersMiddleware>();

        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
            
            // Health checks
            endpoints.MapHealthChecks("/health", new HealthCheckOptions
            {
                ResponseWriter = HealthCheckResponseWriter.WriteResponse
            });
            
            // Detailed health checks
            endpoints.MapHealthChecks("/health/detailed", new HealthCheckOptions
            {
                ResponseWriter = HealthCheckResponseWriter.WriteResponse,
                Predicate = _ => true
            });
        });
    }
}
```

### Deployment Configuration
```csharp
public class Program
{
    public static async Task Main(string[] args)
    {
        var host = CreateHostBuilder(args).Build();

        // Database migration on startup
        using (var scope = host.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await context.Database.MigrateAsync();
        }

        await host.RunAsync();
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseStartup<Startup>();
                webBuilder.ConfigureKestrel(options =>
                {
                    options.AddServerHeader = false;
                    options.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10MB
                });
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.AddDebug();
                logging.AddEventSourceLogger();
            })
            .ConfigureAppConfiguration((context, config) =>
            {
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                      .AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: true)
                      .AddEnvironmentVariables()
                      .AddCommandLine(args);

                if (context.HostingEnvironment.IsDevelopment())
                {
                    config.AddUserSecrets<Program>();
                }
            });
}
```

## 7. Docker Configuration

### Dockerfile
```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["MyApp.csproj", "./"]
RUN dotnet restore "MyApp.csproj"
COPY . .
RUN dotnet build "MyApp.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "MyApp.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "MyApp.dll"]
```

### docker-compose.yml
```yaml
version: '3.8'

services:
  app:
    build: .
    ports:
      - "8080:80"
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - ConnectionStrings__DefaultConnection=Server=db;Database=MyApp;User Id=sa;Password=YourPassword123!
      - ConnectionStrings__Redis=redis:6379
    depends_on:
      - db
      - redis
    restart: unless-stopped

  db:
    image: mcr.microsoft.com/mssql/server:2019-latest
    environment:
      - ACCEPT_EULA=Y
      - SA_PASSWORD=YourPassword123!
    ports:
      - "1433:1433"
    volumes:
      - sqldata:/var/opt/mssql
    restart: unless-stopped

  redis:
    image: redis:6-alpine
    ports:
      - "6379:6379"
    volumes:
      - redisdata:/data
    restart: unless-stopped

volumes:
  sqldata:
  redisdata:
```

This completes our comprehensive ASP.NET Core Advanced step-by-step guide! We've covered:

1. **Phase 1**: Advanced Middleware & Request Pipeline
2. **Phase 2**: Advanced Dependency Injection & Service Patterns  
3. **Phase 3**: Advanced Authentication & Authorization
4. **Phase 4**: Advanced Performance & Caching
5. **Phase 5**: Advanced Architecture Patterns & Production Ready Code

Each phase builds upon the previous ones, providing you with enterprise-level ASP.NET Core development skills. You now have the knowledge to build scalable, secure, and performant web applications using advanced patterns and best practices.

Would you like me to dive deeper into any specific phase or create additional examples for particular scenarios?