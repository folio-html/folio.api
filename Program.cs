using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Folio.Api;

var builder = WebApplication.CreateBuilder(args);

// Journals can carry embedded (base64) images, so allow generous request bodies.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 50L * 1024 * 1024);

builder.Services.AddSingleton<JournalStore>();

builder.Services.AddHealthChecks()
    .AddCheck<DataDirectoryHealthCheck>("data_directory");

// CORS is only needed when folio.html is served from a different origin than this
// API (e.g. opened from file://). When AllowedOrigins is empty the bundled,
// same-origin deployment needs nothing. ETag must be exposed or browsers can't
// read it cross-origin — which would break optimistic concurrency. Cross-origin
// also means the session cookie travels cross-site, so credentials must be
// allowed here (paired with SameSite=None;Secure on the cookie below).
var allowedOrigins = builder.Configuration.GetSection("Folio:AllowedOrigins").Get<string[]>() ?? [];
var crossOrigin = allowedOrigins.Length > 0;
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (!crossOrigin) return;
    p.WithOrigins(allowedOrigins)
     .AllowAnyHeader()
     .AllowCredentials()
     .WithMethods("GET", "HEAD", "PUT", "DELETE", "POST")
     .WithExposedHeaders("ETag");
}));

// Auth is a single shared password (Folio:Password) exchanged for an encrypted,
// HttpOnly session cookie. The cookie is signed/encrypted with Data Protection
// keys persisted under the data dir so sessions survive restarts and redeploys —
// otherwise every container restart would silently log everyone out.
var dataDir = builder.Configuration["Folio:DataDir"] ?? "data";
var dataRoot = Path.GetFullPath(Path.IsPathRooted(dataDir)
    ? dataDir : Path.Combine(builder.Environment.ContentRootPath, dataDir));
var keysDir = Path.Combine(dataRoot, ".keys");
Directory.CreateDirectory(keysDir);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
    .SetApplicationName("folio.api");

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "folio.session";
        o.Cookie.HttpOnly = true;
        // Same-origin (bundled) deployment: Lax is enough and works over plain
        // http in dev. Cross-origin: the cookie must be SameSite=None, which
        // browsers only honour when also Secure — so cross-origin needs HTTPS.
        o.Cookie.SameSite = crossOrigin ? SameSiteMode.None : SameSiteMode.Lax;
        o.Cookie.SecurePolicy = crossOrigin ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        o.SlidingExpiration = true;
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        // This is an API, not a server-rendered app: answer unauthenticated
        // calls with 401/403 instead of redirecting to a login page.
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// Serve folio.html same-origin from wwwroot, so its endpoint auto-detection
// (location.origin) just works with zero client config.
var defaults = new DefaultFilesOptions();
defaults.DefaultFileNames.Clear();
defaults.DefaultFileNames.Add("folio.html");
app.UseDefaultFiles(defaults);
app.UseStaticFiles();

app.MapHealthChecks("/health");

// ── API v1 ───────────────────────────────────────────────────────────────────
// URL-path versioning: every breaking change gets a new prefix (/v2, …) while
// older clients keep hitting their version. The whole group requires a valid
// session cookie (see /v1/auth below).
var v1 = app.MapGroup("/v1/journals").RequireAuthorization();

v1.MapGet("/", (JournalStore store) => Results.Ok(store.List()));

v1.MapGet("/{name}", async (string name, JournalStore store, HttpResponse res, CancellationToken ct) =>
{
    if (!JournalStore.IsValidName(name)) return Results.BadRequest("Invalid journal name.");
    var found = await store.ReadAsync(name, ct);
    if (found is null) return Results.NotFound();
    res.Headers.ETag = found.Value.ETag;
    return Results.Text(Encoding.UTF8.GetString(found.Value.Bytes), "text/markdown", Encoding.UTF8);
});

v1.MapMethods("/{name}", ["HEAD"], async (string name, JournalStore store, HttpResponse res, CancellationToken ct) =>
{
    if (!JournalStore.IsValidName(name)) return Results.BadRequest();
    var etag = await store.ETagAsync(name, ct);
    if (etag is null) return Results.NotFound();
    res.Headers.ETag = etag;
    return Results.Ok();
});

v1.MapPut("/{name}", async (string name, HttpRequest req, HttpResponse res, JournalStore store, CancellationToken ct) =>
{
    if (!JournalStore.IsValidName(name)) return Results.BadRequest("Invalid journal name.");

    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms, ct);

    var ifMatch = req.Headers.IfMatch.ToString();
    var result = await store.WriteAsync(name, ms.ToArray(), string.IsNullOrEmpty(ifMatch) ? null : ifMatch, ct);
    if (result.Outcome == WriteOutcome.Conflict)
        return Results.StatusCode(StatusCodes.Status412PreconditionFailed);

    res.Headers.ETag = result.ETag;
    return result.Outcome == WriteOutcome.Created
        ? Results.Created($"/v1/journals/{name}", null)
        : Results.NoContent();
});

v1.MapDelete("/{name}", async (string name, JournalStore store, CancellationToken ct) =>
{
    if (!JournalStore.IsValidName(name)) return Results.BadRequest();
    return await store.DeleteAsync(name, ct) ? Results.NoContent() : Results.NotFound();
});

// ── Auth ───────────────────────────────────────────────────────────────────────
// A single shared password (Folio:Password, override with Folio__Password in
// deployment) is exchanged for an encrypted, HttpOnly session cookie. The
// /v1/journals group above requires that cookie; the endpoints here issue and
// clear it, so login itself must stay unauthenticated. The password is compared
// in constant time so it can't be timing-probed.
var auth = app.MapGroup("/v1/auth");

auth.MapPost("/login", async (LoginRequest body, HttpContext ctx, IConfiguration config) =>
{
    var expected = config["Folio:Password"];
    if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(body?.Password)
        || !FixedTimeEquals(body.Password, expected))
    {
        return Results.Unauthorized();
    }
    var identity = new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "folio")], CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.NoContent();
});

auth.MapPost("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
});

// Cheap session probe for the frontend: 204 if the cookie is valid, else 401.
auth.MapGet("/session", (HttpContext ctx) =>
    ctx.User.Identity?.IsAuthenticated == true ? Results.NoContent() : Results.Unauthorized());

app.Run();

static bool FixedTimeEquals(string a, string b) =>
    CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

record LoginRequest(string? Password);
