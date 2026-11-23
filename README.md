# User Management API

A small ASP.NET Core Minimal API (.NET 10) for managing users. It has full CRUD endpoints, input validation, and four middleware components (error handling, request logging, rate limiting and token authentication). Data is kept in memory, so no database is needed.

## Run it

```bash
dotnet run
```

This uses the included launch profile (Development environment, http://localhost:5000) and the development token `dev-token-123`. Outside Development the app refuses to start unless a token is configured:

```bash
# Windows PowerShell
$env:Auth__Token = "my-secret-token"; $env:ASPNETCORE_ENVIRONMENT = "Production"; dotnet run

# macOS / Linux
Auth__Token=my-secret-token ASPNETCORE_ENVIRONMENT=Production dotnet run
```

Try the requests in `UserManagementApi.http` (VS Code REST Client or Visual Studio) or use PowerShell:

```powershell
$h = @{ Authorization = "Bearer dev-token-123" }
Invoke-RestMethod -Uri http://localhost:5000/users -Headers $h
```

## Endpoints

| Method | Route | Description | Success | Errors |
|---|---|---|---|---|
| GET | `/health` | Health check (no token needed) | 200 | |
| GET | `/users?page=&pageSize=` | List users with paging (pageSize 1-100) | 200 | 401, 429 |
| GET | `/users/{id}` | Get one user | 200 | 401, 404, 429 |
| POST | `/users` | Create a user | 201 | 400, 401, 409, 413, 429 |
| PUT | `/users/{id}` | Update a user | 200 | 400, 401, 404, 409, 413, 429 |
| DELETE | `/users/{id}` | Delete a user | 204 | 401, 404, 429 |

A user has `id`, `firstName`, `lastName`, `email` and `department`. All errors use the standard ProblemDetails JSON format.

## Validation

Requests to create or update a user are validated with DataAnnotations: first name, last name and department are required (2-50 characters, whitespace-only values are rejected), and email is required, must be a valid address and at most 254 characters. An empty body, a null body or malformed JSON returns 400, a body above 64 KB returns 413, and a duplicate email (case-insensitive) returns 409.

## Middleware

The order matters: `ErrorHandling -> RequestLogging -> RateLimiting -> TokenAuthentication -> endpoints`.

1. **ErrorHandlingMiddleware** turns unhandled exceptions into a generic 500 response (details only in the log) and bad requests (malformed JSON, oversized body) into 400/413.
2. **RequestLoggingMiddleware** logs the method, path, status code and duration of every request, including rejected ones and requests that end with an error.
3. **Rate limiting** (built-in ASP.NET Core) allows 100 requests per minute on `/users` and answers 429 after that (`RateLimit__PermitPerMinute` changes the limit).
4. **TokenAuthenticationMiddleware** requires `Authorization: Bearer <token>` on every endpoint except `/health`. The token is compared in constant time, and a missing or wrong token gets 401.

Outside Development, HTTPS redirection and HSTS are enabled so the token is never sent over plain HTTP.

## Debugging and review with Microsoft Copilot

The first version of `Program.cs` was reviewed with Microsoft Copilot using this prompt: "Review this ASP.NET Core Minimal API for bugs, missing validation, security problems and performance issues. List each problem and show the fix." Copilot returned 14 findings plus two validation notes, rated by severity. I checked each one, applied the ones that were correct, tested them again, and left out a few with a reason.

| Copilot finding | Decision | What changed |
|---|---|---|
| High: the development token is used silently if `Auth:Token` is missing | Applied | Startup fails outside Development; a launch profile keeps `dotnet run` working locally |
| High: race condition, two requests can add the same email | Applied | Check and insert happen atomically under one lock (test: 60 parallel requests with the same email gave one 201 and 59 409) |
| Medium: email check scans every user (O(n)) | Applied | A dictionary index gives O(1) lookups |
| Medium: PUT can recreate a user that was deleted in between | Applied | Existence check and update happen under the same lock (tested with parallel PUT and DELETE) |
| Medium: no rate limiting | Applied | Built-in fixed-window rate limiter, 429 when exceeded |
| Medium: no HTTPS enforcement | Applied | HSTS and HTTPS redirection outside Development |
| Low: no request size limit | Applied | 64 KB limit returns 413 |
| Low: logging is skipped if a later component throws | Applied | `try/finally` in the logging middleware |
| Low: the whole list is copied before paging | Applied | The repository returns only the requested page |
| Low: repeated `Trim()` calls | Applied | Values are normalized once in one method |
| Low: several different error formats | Applied | One ProblemDetails format for all errors |
| High: use the built-in authentication handler instead of custom middleware | Not applied | The assignment asks for middleware, so the custom authentication middleware stays; a built-in JWT or handler is the next step for production |
| Medium: pass `CancellationToken` to endpoints | Not applied | All work is in memory and synchronous, so cancellation would have no effect |
| Low: `/health` is public | Not applied | A public liveness check is intended; it returns no data |
| Validation note: whitespace-only names pass `[Required]` | Not applied | Tested: `[Required]` already rejects whitespace-only strings (400) |
| Validation note: store emails in lower case | Not applied | Emails keep their original casing, and all comparisons are case-insensitive |

While testing the changes I found two more problems myself: malformed JSON returned 500 in the Development environment (fixed in the error handling middleware to return 400), and a very large page number could overflow an integer in the paging calculation (fixed with `long` arithmetic).

## Tests performed

All endpoints and middleware were exercised with real HTTP requests: valid and invalid create, update and delete requests, unknown ids, duplicate emails, malformed JSON, an empty body, an oversized body, paging (including a huge page number), rate limiting, missing or wrong tokens, starting in Production without a token (fails) and with a token (starts), and parallel requests for the race conditions.
