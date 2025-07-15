# Advanced Security Patterns in ASP.NET Core

## 1. Advanced JWT Authentication

### Custom JWT Token Service
```csharp
public interface IJwtTokenService
{
    string GenerateToken(User user, IEnumerable<string> roles);
    string GenerateRefreshToken();
    ClaimsPrincipal GetPrincipalFromExpiredToken(string token);
    bool ValidateToken(string token);
}

public class JwtTokenService : IJwtTokenService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<JwtTokenService> _logger;
    
    public JwtTokenService(IConfiguration configuration, ILogger<JwtTokenService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public string GenerateToken(User user, IEnumerable<string> roles)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Email, user.Email),
            new("jti", Guid.NewGuid().ToString()),
            new("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
        };

        // Add role claims
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"],
            audience: _configuration["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(Convert.ToInt32(_configuration["Jwt:ExpirationMinutes"])),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string GenerateRefreshToken()
    {
        var randomNumber = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomNumber);
        return Convert.ToBase64String(randomNumber);
    }

    public ClaimsPrincipal GetPrincipalFromExpiredToken(string token)
    {
        var tokenValidationParameters = new TokenValidationParameters
        {
            ValidateAudience = false,
            ValidateIssuer = false,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["Jwt:Key"])),
            ValidateLifetime = false // Don't validate lifetime for refresh token scenarios
        };

        var tokenHandler = new JwtSecurityTokenHandler();
        var principal = tokenHandler.ValidateToken(token, tokenValidationParameters, out var securityToken);
        
        if (securityToken is not JwtSecurityToken jwtSecurityToken || 
            !jwtSecurityToken.Header.Alg.Equals(SecurityAlgorithms.HmacSha256, StringComparison.InvariantCultureIgnoreCase))
        {
            throw new SecurityTokenException("Invalid token");
        }

        return principal;
    }

    public bool ValidateToken(string token)
    {
        try
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var key = Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]);
            
            tokenHandler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(key),
                ValidateIssuer = true,
                ValidIssuer = _configuration["Jwt:Issuer"],
                ValidateAudience = true,
                ValidAudience = _configuration["Jwt:Audience"],
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            }, out var validatedToken);

            return true;
        }
        catch
        {
            return false;
        }
    }
}
```

### JWT Refresh Token Implementation
```csharp
public class RefreshTokenService : IRefreshTokenService
{
    private readonly IRefreshTokenRepository _refreshTokenRepository;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly IUserService _userService;
    
    public RefreshTokenService(
        IRefreshTokenRepository refreshTokenRepository,
        IJwtTokenService jwtTokenService,
        IUserService userService)
    {
        _refreshTokenRepository = refreshTokenRepository;
        _jwtTokenService = jwtTokenService;
        _userService = userService;
    }

    public async Task<TokenResponse> RefreshTokenAsync(string accessToken, string refreshToken)
    {
        var principal = _jwtTokenService.GetPrincipalFromExpiredToken(accessToken);
        var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        
        if (userId == null)
            throw new SecurityTokenException("Invalid token");

        var storedRefreshToken = await _refreshTokenRepository.GetByTokenAsync(refreshToken);
        
        if (storedRefreshToken == null || 
            storedRefreshToken.UserId != int.Parse(userId) ||
            storedRefreshToken.IsExpired ||
            storedRefreshToken.IsRevoked)
        {
            throw new SecurityTokenException("Invalid refresh token");
        }

        // Revoke old refresh token
        storedRefreshToken.IsRevoked = true;
        await _refreshTokenRepository.UpdateAsync(storedRefreshToken);

        // Generate new tokens
        var user = await _userService.GetByIdAsync(int.Parse(userId));
        var roles = await _userService.GetUserRolesAsync(user.Id);
        
        var newAccessToken = _jwtTokenService.GenerateToken(user, roles);
        var newRefreshToken = _jwtTokenService.GenerateRefreshToken();

        // Store new refresh token
        await _refreshTokenRepository.CreateAsync(new RefreshToken
        {
            Token = newRefreshToken,
            UserId = user.Id,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedAt = DateTime.UtcNow
        });

        return new TokenResponse
        {
            AccessToken = newAccessToken,
            RefreshToken = newRefreshToken
        };
    }

    public async Task RevokeTokenAsync(string refreshToken)
    {
        var token = await _refreshTokenRepository.GetByTokenAsync(refreshToken);
        if (token != null)
        {
            token.IsRevoked = true;
            await _refreshTokenRepository.UpdateAsync(token);
        }
    }
}
```

## 2. Custom Authorization Policies

### Policy-Based Authorization
```csharp
public class MinimumAgeRequirement : IAuthorizationRequirement
{
    public int MinimumAge { get; }

    public MinimumAgeRequirement(int minimumAge)
    {
        MinimumAge = minimumAge;
    }
}

public class MinimumAgeHandler : AuthorizationHandler<MinimumAgeRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        MinimumAgeRequirement requirement)
    {
        var dateOfBirth = context.User.FindFirst(c => c.Type == "DateOfBirth")?.Value;
        
        if (dateOfBirth == null)
        {
            context.Fail();
            return Task.CompletedTask;
        }

        if (DateTime.TryParse(dateOfBirth, out var birthDate))
        {
            var age = DateTime.Today.Year - birthDate.Year;
            if (birthDate > DateTime.Today.AddYears(-age))
                age--;

            if (age >= requirement.MinimumAge)
            {
                context.Succeed(requirement);
            }
        }

        return Task.CompletedTask;
    }
}

// Resource-based authorization
public class DocumentAuthorizationHandler : AuthorizationHandler<OperationAuthorizationRequirement, Document>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        OperationAuthorizationRequirement requirement,
        Document resource)
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        
        if (userId == null)
        {
            context.Fail();
            return Task.CompletedTask;
        }

        if (requirement.Name == "Read")
        {
            if (resource.IsPublic || resource.OwnerId == int.Parse(userId))
            {
                context.Succeed(requirement);
            }
        }
        else if (requirement.Name == "Edit" || requirement.Name == "Delete")
        {
            if (resource.OwnerId == int.Parse(userId) || context.User.IsInRole("Admin"))
            {
                context.Succeed(requirement);
            }
        }

        return Task.CompletedTask;
    }
}
```

### Dynamic Authorization
```csharp
public class DynamicAuthorizationPolicyProvider : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallbackPolicyProvider;
    
    public DynamicAuthorizationPolicyProvider(IOptions<AuthorizationOptions> options)
    {
        _fallbackPolicyProvider = new DefaultAuthorizationPolicyProvider(options);
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync()
    {
        return _fallbackPolicyProvider.GetDefaultPolicyAsync();
    }

    public Task<AuthorizationPolicy> GetFallbackPolicyAsync()
    {
        return _fallbackPolicyProvider.GetFallbackPolicyAsync();
    }

    public Task<AuthorizationPolicy> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith("Permission"))
        {
            var policy = new AuthorizationPolicyBuilder();
            policy.AddRequirements(new PermissionRequirement(policyName));
            return Task.FromResult(policy.Build());
        }

        return _fallbackPolicyProvider.GetPolicyAsync(policyName);
    }
}

public class PermissionRequirement : IAuthorizationRequirement
{
    public string Permission { get; }

    public PermissionRequirement(string permission)
    {
        Permission = permission;
    }
}

public class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly IPermissionService _permissionService;
    
    public PermissionHandler(IPermissionService permissionService)
    {
        _permissionService = permissionService;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        
        if (userId == null)
        {
            context.Fail();
            return;
        }

        var hasPermission = await _permissionService.UserHasPermissionAsync(
            int.Parse(userId), 
            requirement.Permission);

        if (hasPermission)
        {
            context.Succeed(requirement);
        }
    }
}
```

## 3. OAuth2 and OpenID Connect

### OAuth2 Integration
```csharp
public class OAuth2Configuration
{
    public string ClientId { get; set; }
    public string ClientSecret { get; set; }
    public string AuthorizeEndpoint { get; set; }
    public string TokenEndpoint { get; set; }
    public string RedirectUri { get; set; }
    public string[] Scopes { get; set; }
}

public class OAuth2Service : IOAuth2Service
{
    private readonly HttpClient _httpClient;
    private readonly OAuth2Configuration _config;
    private readonly ILogger<OAuth2Service> _logger;
    
    public OAuth2Service(HttpClient httpClient, IOptions<OAuth2Configuration> config, ILogger<OAuth2Service> logger)
    {
        _httpClient = httpClient;
        _config = config.Value;
        _logger = logger;
    }

    public string GetAuthorizationUrl(string state)
    {
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = _config.ClientId,
            ["redirect_uri"] = _config.RedirectUri,
            ["response_type"] = "code",
            ["scope"] = string.Join(" ", _config.Scopes),
            ["state"] = state
        };

        var query = string.Join("&", parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        return $"{_config.AuthorizeEndpoint}?{query}";
    }

    public async Task<TokenResponse> ExchangeCodeForTokenAsync(string code, string state)
    {
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = _config.ClientId,
            ["client_secret"] = _config.ClientSecret,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = _config.RedirectUri
        };

        var content = new FormUrlEncodedContent(parameters);
        var response = await _httpClient.PostAsync(_config.TokenEndpoint, content);
        
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new OAuth2Exception($"Token exchange failed: {error}");
        }

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<TokenResponse>(json);
    }

    public async Task<UserInfo> GetUserInfoAsync(string accessToken)
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        
        var response = await _httpClient.GetAsync("https://api.provider.com/userinfo");
        
        if (!response.IsSuccessStatusCode)
        {
            throw new OAuth2Exception("Failed to get user info");
        }

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<UserInfo>(json);
    }
}
```

### OpenID Connect Integration
```csharp
public void ConfigureServices(IServiceCollection services)
{
    services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
    {
        options.Authority = "https://identity.provider.com";
        options.ClientId = "your-client-id";
        options.ClientSecret = "your-client-secret";
        options.ResponseType = "code";
        options.SaveTokens = true;
        options.GetClaimsFromUserInfoEndpoint = true;
        
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        
        options.Events = new OpenIdConnectEvents
        {
            OnTokenValidated = async context =>
            {
                // Custom logic after token validation
                var userService = context.HttpContext.RequestServices.GetRequiredService<IUserService>();
                var user = await userService.GetOrCreateUserAsync(context.Principal);
                
                // Add custom claims
                var claims = new List<Claim>
                {
                    new("custom_claim", "custom_value")
                };
                
                var appIdentity = new ClaimsIdentity(claims);
                context.Principal.AddIdentity(appIdentity);
            },
            
            OnRedirectToIdentityProvider = context =>
            {
                // Customize redirect parameters
                context.ProtocolMessage.SetParameter("custom_param", "custom_value");
                return Task.CompletedTask;
            }
        };
    });
}
```

## 4. Advanced Security Middleware

### API Key Authentication Middleware
```csharp
public class ApiKeyAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IApiKeyService _apiKeyService;
    private readonly ILogger<ApiKeyAuthenticationMiddleware> _logger;
    
    public ApiKeyAuthenticationMiddleware(
        RequestDelegate next,
        IApiKeyService apiKeyService,
        ILogger<ApiKeyAuthenticationMiddleware> logger)
    {
        _next = next;
        _apiKeyService = apiKeyService;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Headers.ContainsKey("X-API-Key"))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("API Key is missing");
            return;
        }

        var apiKey = context.Request.Headers["X-API-Key"].FirstOrDefault();
        
        var keyInfo = await _apiKeyService.ValidateApiKeyAsync(apiKey);
        
        if (keyInfo == null)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Invalid API Key");
            return;
        }

        if (keyInfo.IsExpired || !keyInfo.IsActive)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("API Key is expired or inactive");
            return;
        }

        // Add API key info to context
        context.Items["ApiKey"] = keyInfo;
        
        // Create claims principal
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, keyInfo.Name),
            new Claim("ApiKeyId", keyInfo.Id.ToString()),
            new Claim("ClientId", keyInfo.ClientId)
        };

        var identity = new ClaimsIdentity(claims, "ApiKey");
        var principal = new ClaimsPrincipal(identity);
        context.User = principal;

        await _next(context);
    }
}
```

### Security Headers Middleware
```csharp
public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SecurityHeadersOptions _options;
    
    public SecurityHeadersMiddleware(RequestDelegate next, IOptions<SecurityHeadersOptions> options)
    {
        _next = next;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Add security headers
        context.Response.Headers.Add("X-Content-Type-Options", "nosniff");
        context.Response.Headers.Add("X-Frame-Options", "DENY");
        context.Response.Headers.Add("X-XSS-Protection", "1; mode=block");
        context.Response.Headers.Add("Referrer-Policy", "strict-origin-when-cross-origin");
        
        if (_options.UseHsts)
        {
            context.Response.Headers.Add("Strict-Transport-Security", "max-age=31536000; includeSubDomains");
        }
        
        if (!string.IsNullOrEmpty(_options.ContentSecurityPolicy))
        {
            context.Response.Headers.Add("Content-Security-Policy", _options.ContentSecurityPolicy);
        }

        await _next(context);
    }
}

public class SecurityHeadersOptions
{
    public bool UseHsts { get; set; } = true;
    public string ContentSecurityPolicy { get; set; } = "default-src 'self'";
}
```

## 5. Registration and Configuration

### Complete Security Setup
```csharp
public void ConfigureServices(IServiceCollection services)
{
    // JWT Configuration
    services.Configure<JwtConfiguration>(Configuration.GetSection("Jwt"));
    services.AddScoped<IJwtTokenService, JwtTokenService>();
    services.AddScoped<IRefreshTokenService, RefreshTokenService>();
    
    // Authentication
    services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
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
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Configuration["Jwt:Key"])),
            ClockSkew = TimeSpan.Zero
        };
        
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var userService = context.HttpContext.RequestServices.GetRequiredService<IUserService>();
                var userId = context.Principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                
                if (userId != null)
                {
                    var user = await userService.GetByIdAsync(int.Parse(userId));
                    if (user == null || !user.IsActive)
                    {
                        context.Fail("User not found or inactive");
                    }
                }
            }
        };
    });

    // Authorization
    services.AddAuthorization(options =>
    {
        options.AddPolicy("MinimumAge18", policy =>
            policy.Requirements.Add(new MinimumAgeRequirement(18)));
        
        options.AddPolicy("AdminOnly", policy =>
            policy.RequireRole("Admin"));
        
        options.AddPolicy("PermissionBased", policy =>
            policy.Requirements.Add(new PermissionRequirement("Permission")));
    });
    
    // Authorization handlers
    services.AddScoped<IAuthorizationHandler, MinimumAgeHandler>();
    services.AddScoped<IAuthorizationHandler, PermissionHandler>();
    services.AddScoped<IAuthorizationHandler, DocumentAuthorizationHandler>();
    
    // Dynamic policy provider
    services.AddSingleton<IAuthorizationPolicyProvider, DynamicAuthorizationPolicyProvider>();
    
    // OAuth2
    services.Configure<OAuth2Configuration>(Configuration.GetSection("OAuth2"));
    services.AddHttpClient<IOAuth2Service, OAuth2Service>();
    
    // Security headers
    services.Configure<SecurityHeadersOptions>(Configuration.GetSection("SecurityHeaders"));
    
    // API Key service
    services.AddScoped<IApiKeyService, ApiKeyService>();
}

public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
{
    // Security middleware
    app.UseMiddleware<SecurityHeadersMiddleware>();
    
    // HTTPS redirection
    app.UseHttpsRedirection();
    
    // Authentication & Authorization
    app.UseAuthentication();
    app.UseAuthorization();
    
    // API Key middleware for specific routes
    app.MapWhen(context => context.Request.Path.StartsWithSegments("/api/external"),
        apiApp => apiApp.UseMiddleware<ApiKeyAuthenticationMiddleware>());
    
    app.UseRouting();
    app.UseEndpoints(endpoints =>
    {
        endpoints.MapControllers();
    });
}
```

## 6. Usage Examples

### Controller with Advanced Authorization
```csharp
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DocumentsController : ControllerBase
{
    private readonly IDocumentService _documentService;
    private readonly IAuthorizationService _authorizationService;
    
    public DocumentsController(IDocumentService documentService, IAuthorizationService authorizationService)
    {
        _documentService = documentService;
        _authorizationService = authorizationService;
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Document>> GetDocument(int id)
    {
        var document = await _documentService.GetByIdAsync(id);
        
        if (document == null)
            return NotFound();

        var authResult = await _authorizationService.AuthorizeAsync(User, document, "Read");
        
        if (!authResult.Succeeded)
            return Forbid();

        return Ok(document);
    }

    [HttpPost]
    [Authorize(Policy = "MinimumAge18")]
    public async Task<ActionResult<Document>> CreateDocument([FromBody] CreateDocumentRequest request)
    {
        var document = await _documentService.CreateAsync(request);
        return CreatedAtAction(nameof(GetDocument), new { id = document.Id }, document);
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "PermissionEditDocument")]
    public async Task<ActionResult> UpdateDocument(int id, [FromBody] UpdateDocumentRequest request)
    {
        var document = await _documentService.GetByIdAsync(id);
        
        if (document == null)
            return NotFound();

        var authResult = await _authorizationService.AuthorizeAsync(User, document, "Edit");
        
        if (!authResult.Succeeded)
            return Forbid();

        await _documentService.UpdateAsync(id, request);
        return NoContent();
    }
}
```