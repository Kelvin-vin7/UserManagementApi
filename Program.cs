using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Token comes from configuration / environment variable (Auth__Token). A development default is used only if none is set.
var apiToken = builder.Configuration["Auth:Token"];
if (string.IsNullOrWhiteSpace(apiToken))
{
    apiToken = "dev-token-123";
    Console.WriteLine("WARNING: Auth:Token is not set. Using the development token. Set Auth__Token for real use.");
}

builder.Services.AddSingleton<IUserRepository, InMemoryUserRepository>();

var app = builder.Build();

// ---- Middleware pipeline (order matters) --------------------------------------------------
// 1. Error handling first, so it catches exceptions from everything after it.
// 2. Logging next, so even rejected (401) requests are logged.
// 3. Authentication last, right before the endpoints.
app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseMiddleware<TokenAuthenticationMiddleware>(apiToken);

// ---- Endpoints ------------------------------------------------------------------------------
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

var users = app.MapGroup("/users");

// GET /users?page=1&pageSize=20 - list with paging (limits memory use and response size)
users.MapGet("/", (IUserRepository repo, int? page, int? pageSize) =>
{
    var p = Math.Max(page ?? 1, 1);
    var size = Math.Clamp(pageSize ?? 20, 1, 100);
    var all = repo.GetAll();
    var items = all.Skip((p - 1) * size).Take(size).ToList();
    return Results.Ok(new { page = p, pageSize = size, total = all.Count, items });
});

// GET /users/{id}
users.MapGet("/{id:int}", (int id, IUserRepository repo) =>
    repo.Get(id) is { } user ? Results.Ok(user) : Results.NotFound(Error($"User {id} was not found.")));

// POST /users
users.MapPost("/", (UserRequest? request, IUserRepository repo) =>
{
    var errors = Validate(request);
    if (errors is not null) return Results.ValidationProblem(errors);

    if (repo.EmailExists(request!.Email))
        return Results.Conflict(Error("A user with this email already exists."));

    var created = repo.Add(request);
    return Results.Created($"/users/{created.Id}", created);
});

// PUT /users/{id}
users.MapPut("/{id:int}", (int id, UserRequest? request, IUserRepository repo) =>
{
    var errors = Validate(request);
    if (errors is not null) return Results.ValidationProblem(errors);

    if (repo.Get(id) is null)
        return Results.NotFound(Error($"User {id} was not found."));
    if (repo.EmailExists(request!.Email, ignoreUserId: id))
        return Results.Conflict(Error("Another user already uses this email."));

    return Results.Ok(repo.Update(id, request));
});

// DELETE /users/{id}
users.MapDelete("/{id:int}", (int id, IUserRepository repo) =>
    repo.Delete(id) ? Results.NoContent() : Results.NotFound(Error($"User {id} was not found.")));

app.Run();

// ---- Helpers --------------------------------------------------------------------------------
static object Error(string message) => new { error = message };

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

// ---- Models ---------------------------------------------------------------------------------
public record User(int Id, string FirstName, string LastName, string Email, string Department);

public class UserRequest
{
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
public interface IUserRepository
{
    IReadOnlyList<User> GetAll();
    User? Get(int id);
    bool EmailExists(string email, int? ignoreUserId = null);
    User Add(UserRequest request);
    User Update(int id, UserRequest request);
    bool Delete(int id);
}

public class InMemoryUserRepository : IUserRepository
{
    private readonly ConcurrentDictionary<int, User> _users = new();
    private int _nextId;

    public InMemoryUserRepository()
    {
        Add(new UserRequest { FirstName = "Alice", LastName = "Johnson", Email = "alice@techhive.example", Department = "HR" });
        Add(new UserRequest { FirstName = "Bob", LastName = "Smith", Email = "bob@techhive.example", Department = "IT" });
    }

    public IReadOnlyList<User> GetAll() => _users.Values.OrderBy(u => u.Id).ToList();

    public User? Get(int id) => _users.TryGetValue(id, out var user) ? user : null;

    public bool EmailExists(string email, int? ignoreUserId = null) =>
        _users.Values.Any(u => u.Id != ignoreUserId && string.Equals(u.Email, email.Trim(), StringComparison.OrdinalIgnoreCase));

    public User Add(UserRequest r)
    {
        var id = Interlocked.Increment(ref _nextId);            // thread-safe unique ids
        var user = new User(id, r.FirstName.Trim(), r.LastName.Trim(), r.Email.Trim(), r.Department.Trim());
        _users[id] = user;
        return user;
    }

    public User Update(int id, UserRequest r)
    {
        var user = new User(id, r.FirstName.Trim(), r.LastName.Trim(), r.Email.Trim(), r.Department.Trim());
        _users[id] = user;
        return user;
    }

    public bool Delete(int id) => _users.TryRemove(id, out _);
}

// ---- Middleware -------------------------------------------------------------------------------
// Catches unhandled exceptions and returns a consistent JSON error instead of a stack trace.
public class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
            if (context.Response.HasStarted) throw;
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new { error = "Internal server error." });
        }
    }
}

// Logs method, path, status code and duration of every request.
public class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var timer = Stopwatch.StartNew();
        await next(context);
        timer.Stop();
        logger.LogInformation("{Method} {Path} -> {StatusCode} in {Elapsed} ms",
            context.Request.Method, context.Request.Path, context.Response.StatusCode, timer.ElapsedMilliseconds);
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

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await context.Response.WriteAsJsonAsync(new { error = "Unauthorized. A valid Bearer token is required." });
    }
}
