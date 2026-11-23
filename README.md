# User Management API

A small ASP.NET Core Minimal API (.NET 10) for managing users. It has full CRUD endpoints, input validation, and three middleware components (error handling, request logging and token authentication). Data is kept in memory, so no database is needed.

## Run it

```bash
dotnet run
```

The API listens on the URL printed in the console. The default development token is `dev-token-123`. To use your own token:

```bash
# Windows PowerShell
$env:Auth__Token = "my-secret-token"; dotnet run

# macOS / Linux
Auth__Token=my-secret-token dotnet run
```

Try the requests in `UserManagementApi.http` (VS Code REST Client or Visual Studio) or use curl:

```bash
curl -H "Authorization: Bearer dev-token-123" http://localhost:5000/users
```

## Endpoints

| Method | Route | Description | Success | Errors |
|---|---|---|---|---|
| GET | `/health` | Health check (no token needed) | 200 | |
| GET | `/users?page=&pageSize=` | List users with paging (pageSize 1-100) | 200 | 401 |
| GET | `/users/{id}` | Get one user | 200 | 401, 404 |
| POST | `/users` | Create a user | 201 | 400, 401, 409 |
| PUT | `/users/{id}` | Update a user | 200 | 400, 401, 404, 409 |
| DELETE | `/users/{id}` | Delete a user | 204 | 401, 404 |

A user has `id`, `firstName`, `lastName`, `email` and `department`.

## Validation

Requests to create or update a user are validated with DataAnnotations: first name, last name and department are required (2-50 characters), and email is required, must be a valid address and at most 254 characters. An empty or null body returns 400. Duplicate emails (case-insensitive) return 409. Invalid data returns a clear 400 message per field instead of being stored.

## Middleware

The order matters: `ErrorHandling -> RequestLogging -> TokenAuthentication -> endpoints`.

1. **ErrorHandlingMiddleware** catches any unhandled exception, logs it, and returns a consistent JSON `500` response without a stack trace.
2. **RequestLoggingMiddleware** logs the method, path, status code and duration of every request, including rejected ones.
3. **TokenAuthenticationMiddleware** requires `Authorization: Bearer <token>` on every endpoint except `/health`. The token is compared in constant time and invalid or missing tokens get `401`.

## Debugging with an AI assistant

The code was reviewed with an AI coding assistant for bugs that are common in user APIs. These problems were checked and are handled, and each fix was tested before it was kept.

| Problem checked | Fix |
|---|---|
| Creating a user with a missing or null body could crash the endpoint (NullReferenceException) | Body is checked first and a 400 validation message is returned |
| Invalid data such as an empty name or a bad email was stored | DataAnnotations validation returns 400 with a message for each field |
| Two users could register the same email | Case-insensitive duplicate check returns 409 (also on update, ignoring the user's own record) |
| Unknown ids returned 200 with empty data or threw an exception | GET, PUT and DELETE return 404 with a clear message |
| Ids could repeat when requests arrived at the same time | Thread-safe ConcurrentDictionary and Interlocked.Increment for ids |
| Unhandled errors exposed a stack trace | Error handling middleware returns a generic 500 and logs the details |
| Listing returned every user at once | Paging with limits on page size |
| Token comparison with == can leak timing information | Constant-time comparison with CryptographicOperations.FixedTimeEquals |

## Tests performed

All endpoints and the middleware were exercised with real HTTP requests: valid and invalid create, update and delete requests, unknown ids, duplicate emails, a malformed JSON body, an empty body, paging, and requests with a missing or wrong token. Results matched the table above, and the request log shows one line per request.
