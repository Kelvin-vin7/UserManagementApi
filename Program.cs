using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// The token comes from configuration / environment variable (Auth__Token).
// The development fallback is allowed ONLY in the Development environment; otherwise startup fails.
var apiToken = builder.Configuration["Auth:Token"];
if (string.IsNullOrWhiteSpace(apiToken))
{
    if (!builder.Environment.IsDevelopment())
        throw new InvalidOperationException("Auth:Token must be configured outside Development (set the Auth__Token environment variable).");

    apiToken = "dev-token-123";
    Console.WriteLine("WARNING: Auth:Token is not set. Using the development token (Development environment only).");
}

// Reject oversized request bodies before they are read into memory.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);

builder.Services.AddSingleton<IUserRepository, InMemoryUserRepository>();

// Rate limiting: a fixed window per application (limit can be changed with RateLimit__PermitPerMinute).
var permitPerMinute = builder.Configuration.GetValue("RateLimit:PermitPerMinute", 100);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (ctx, _) => new ValueTask(
        Problems.Write(ctx.HttpContext, StatusCodes.Status429TooManyRequests, "Too Many Requests", "Rate limit exceeded. Try again later."));
    options.AddFixedWindowLimiter("api", o =>
    {
        o.Window = TimeSpan.FromMinutes(1);
        o.PermitLimit = permitPerMinute;
        o.QueueLimit = 0;
    });
});

var app = builder.Build();

// ---- Middleware pipeline (order matters) --------------------------------------------------
// HTTPS is enforced outside Development (the Bearer token must never travel unencrypted).
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
// 1. Error handling first, so it catches exceptions from everything after it.
// 2. Logging next, so even rejected requests (401, 429) are logged.
// 3. Rate limiting, then authentication, right before the endpoints.
app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseRateLimiter();
app.UseMiddleware<TokenAuthenticationMiddleware>(apiToken);

// ---- Endpoints ------------------------------------------------------------------------------
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

var users = app.MapGroup("/users").RequireRateLimiting("api");

// GET /users?page=1&pageSize=20 - the repository returns only the requested page
users.MapGet("/", (IUserRepository repo, int? page, int? pageSize) =>
{
    var p = Math.Max(page ?? 1, 1);
    var size = Math.Clamp(pageSize ?? 20, 1, 100);
    var (items, total) = repo.GetPage(p, size);
    return Results.Ok(new { page = p, pageSize = size, total, items });
});

// GET /users/{id}
users.MapGet("/{id:int}", (int id, IUserRepository repo) =>
    repo.Get(id) is { } user ? Results.Ok(user) : Problems.NotFound($"User {id} was not found."));

// POST /users
users.MapPost("/", (UserRequest? request, IUserRepository repo) =>
{
    var errors = Validate(request);
    if (errors is not null) return Results.ValidationProblem(errors);

    // Check-and-insert is one atomic operation inside the repository (no race between two requests).
    return repo.TryAdd(request!, out var created)
        ? Results.Created($"/users/{created!.Id}", created)
        : Problems.Conflict("A user with this email already exists.");
});

// PUT /users/{id}
users.MapPut("/{id:int}", (int id, UserRequest? request, IUserRepository repo) =>
{
    var errors = Validate(request);
    if (errors is not null) return Results.ValidationProblem(errors);

    return repo.TryUpdate(id, request!, out var updated) switch
    {
        UpdateResult.Updated => Results.Ok(updated),
        UpdateResult.EmailTaken => Problems.Conflict("Another user already uses this email."),
        _ => Problems.NotFound($"User {id} was not found.")
    };
});

// DELETE /users/{id}
users.MapDelete("/{id:int}", (int id, IUserRepository repo) =>
    repo.Delete(id) ? Results.NoContent() : Problems.NotFound($"User {id} was not found."));

app.Run();

// ---- Helpers --------------------------------------------------------------------------------
// Validates a request with its DataAnnotations. Returns null when valid.
static Dictionary<string, string[]>? Validate(UserRequest? request)
{
    if (request is null)
        return new Dictionary<string, string[]> { ["body"] = new[] { "A request body is required." } };

    var results = new List<ValidationResult>();
    if (Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true))
        return null;

    return results
        .SelectMany(r => (r.MemberNames.Any() ? r.MemberNames : new[] { "body" }).Select(m => (Member: m, Message: r.ErrorMessage ?? "Invalid value.")))
        .GroupBy(x => x.Member)
        .ToDictionary(g => g.Key, g => g.Select(x => x.Message).ToArray());
}

// One error format (RFC 7807 ProblemDetails) for every error response.
public static class Problems
{
    public static IResult NotFound(string detail) => Results.Problem(detail: detail, statusCode: StatusCodes.Status404NotFound, title: "Not Found");
    public static IResult Conflict(string detail) => Results.Problem(detail: detail, statusCode: StatusCodes.Status409Conflict, title: "Conflict");
    public static Task Write(HttpContext context, int status, string title, string detail) =>
        Results.Problem(detail: detail, statusCode: status, title: title).ExecuteAsync(context);
}

// ---- Models ---------------------------------------------------------------------------------
public record User(int Id, string FirstName, string LastName, string Email, string Department);

public class UserRequest
{
    // [Required] already rejects empty and whitespace-only strings.
    [Required(ErrorMessage = "First name is required.")]
    [StringLength(50, MinimumLength = 2, ErrorMessage = "First name must be 2-50 characters.")]
    public string FirstName { get; set; } = "";

    [Required(ErrorMessage = "Last name is required.")]
    [StringLength(50, MinimumLength = 2, ErrorMessage = "Last name must be 2-50 characters.")]
    public string LastName { get; set; } = "";

    [Required(ErrorMessage = "Email is required.")]
    [EmailAddress(ErrorMessage = "Email address is not valid.")]
    [StringLength(254, ErrorMessage = "Email is too long.")]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Department is required.")]
    [StringLength(50, MinimumLength = 2, ErrorMessage = "Department must be 2-50 characters.")]
    public string Department { get; set; } = "";
}

// ---- Data access (thread-safe, in-memory) ----------------------------------------------------
public enum UpdateResult { Updated, NotFound, EmailTaken }

public interface IUserRepository
{
    (IReadOnlyList<User> Items, int Total) GetPage(int page, int pageSize);
    User? Get(int id);
    bool TryAdd(UserRequest request, out User? created);
    UpdateResult TryUpdate(int id, UserRequest request, out User? updated);
    bool Delete(int id);
}

public class InMemoryUserRepository : IUserRepository
{
    // One lock protects the users, the email index and the id counter, so every
    // check-and-change (email uniqueness, update of an existing user) is atomic.
    private readonly object _lock = new();
    private readonly SortedDictionary<int, User> _users = new();
    private readonly Dictionary<string, int> _emailIndex = new(StringComparer.OrdinalIgnoreCase);   // O(1) email lookup
    private int _nextId;

    public InMemoryUserRepository()
    {
        TryAdd(new UserRequest { FirstName = "Alice", LastName = "Johnson", Email = "alice@techhive.example", Department = "HR" }, out _);
        TryAdd(new UserRequest { FirstName = "Bob", LastName = "Smith", Email = "bob@techhive.example", Department = "IT" }, out _);
    }

    public (IReadOnlyList<User> Items, int Total) GetPage(int page, int pageSize)
    {
        // long arithmetic: a huge page number must not overflow int and wrap around to a negative skip.
        var skip = (int)Math.Min((long)(page - 1) * pageSize, int.MaxValue);
        lock (_lock)
            return (_users.Values.Skip(skip).Take(pageSize).ToList(), _users.Count);   // only one page is copied
    }

    public User? Get(int id)
    {
        lock (_lock) return _users.TryGetValue(id, out var user) ? user : null;
    }

    public bool TryAdd(UserRequest request, out User? created)
    {
        var (first, last, email, department) = Normalize(request);
        lock (_lock)
        {
            if (_emailIndex.ContainsKey(email)) { created = null; return false; }
            var id = ++_nextId;
            created = new User(id, first, last, email, department);
            _users[id] = created;
            _emailIndex[email] = id;
            return true;
        }
    }

    public UpdateResult TryUpdate(int id, UserRequest request, out User? updated)
    {
        var (first, last, email, department) = Normalize(request);
        lock (_lock)
        {
            updated = null;
            if (!_users.TryGetValue(id, out var existing)) return UpdateResult.NotFound;   // deleted in the meantime: never recreated
            if (_emailIndex.TryGetValue(email, out var owner) && owner != id) return UpdateResult.EmailTaken;

            _emailIndex.Remove(existing.Email);
            _emailIndex[email] = id;
            updated = new User(id, first, last, email, department);
            _users[id] = updated;
            return UpdateResult.Updated;
        }
    }

    public bool Delete(int id)
    {
        lock (_lock)
        {
            if (!_users.Remove(id, out var removed)) return false;
            _emailIndex.Remove(removed.Email);
            return true;
        }
    }

    // Trim once, in one place.
    private static (string First, string Last, string Email, string Department) Normalize(UserRequest r) =>
        (r.FirstName.Trim(), r.LastName.Trim(), r.Email.Trim(), r.Department.Trim());
}

// ---- Middleware -------------------------------------------------------------------------------
// Catches unhandled exceptions and returns a consistent ProblemDetails error instead of a stack trace.
public class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (BadHttpRequestException ex)
        {
            // Malformed JSON, oversized body, etc.: a client error (400/413), not a server error.
            if (context.Response.HasStarted) throw;
            context.Response.Clear();
            await Problems.Write(context, ex.StatusCode, ex.StatusCode == StatusCodes.Status413PayloadTooLarge ? "Payload Too Large" : "Bad Request",
                ex.StatusCode == StatusCodes.Status413PayloadTooLarge ? "The request body is too large." : "The request could not be read. Check that the JSON is valid.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
            if (context.Response.HasStarted) throw;
            context.Response.Clear();
            await Problems.Write(context, StatusCodes.Status500InternalServerError, "Internal Server Error", "An unexpected error occurred.");
        }
    }
}

// Logs method, path, status code and duration of every request, even when a later component throws.
public class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var timer = Stopwatch.StartNew();
        var status = StatusCodes.Status500InternalServerError;   // used if next() throws
        try
        {
            await next(context);
            status = context.Response.StatusCode;
        }
        finally
        {
            timer.Stop();
            logger.LogInformation("{Method} {Path} -> {StatusCode} in {Elapsed} ms",
                context.Request.Method, context.Request.Path, status, timer.ElapsedMilliseconds);
        }
    }
}

// Requires "Authorization: Bearer <token>" on every endpoint except /health.
public class TokenAuthenticationMiddleware(RequestDelegate next, string expectedToken)
{
    private readonly byte[] _expected = Encoding.UTF8.GetBytes(expectedToken);

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await next(context);
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var supplied = Encoding.UTF8.GetBytes(header[prefix.Length..].Trim());
            // Constant-time comparison, so response time does not reveal how much of the token matched.
            if (CryptographicOperations.FixedTimeEquals(supplied, _expected))
            {
                await next(context);
                return;
            }
        }

        context.Response.Headers.WWWAuthenticate = "Bearer";
        await Problems.Write(context, StatusCodes.Status401Unauthorized, "Unauthorized", "A valid Bearer token is required.");
    }
}
