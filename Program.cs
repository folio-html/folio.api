using System.Security.Cryptography;
using System.Text;
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
// read it cross-origin — which would break optimistic concurrency.
var allowedOrigins = builder.Configuration.GetSection("Folio:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (allowedOrigins.Length == 0) return;
    p.WithOrigins(allowedOrigins)
     .AllowAnyHeader()
     .WithMethods("GET", "HEAD", "PUT", "DELETE")
     .WithExposedHeaders("ETag");
}));

var app = builder.Build();

app.UseCors();

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
// older clients keep hitting their version. The whole group is bearer-protected.
var v1 = app.MapGroup("/v1/journals").AddEndpointFilter(BearerAuth);

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

app.Run();

// ── Auth ───────────────────────────────────────────────────────────────────────
// Static bearer token from config (override with the Folio__Token env var in
// deployment). Compared in constant time so the check can't be timing-probed.
static async ValueTask<object?> BearerAuth(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
{
    var expected = ctx.HttpContext.RequestServices.GetRequiredService<IConfiguration>()["Folio:Token"];
    const string prefix = "Bearer ";
    var header = ctx.HttpContext.Request.Headers.Authorization.ToString();

    if (string.IsNullOrEmpty(expected) || !header.StartsWith(prefix, StringComparison.Ordinal)
        || !FixedTimeEquals(header[prefix.Length..], expected))
    {
        return Results.Unauthorized();
    }
    return await next(ctx);
}

static bool FixedTimeEquals(string a, string b) =>
    CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
