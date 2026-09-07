using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using PiholeReportServer.Configuration;
using PiholeReportServer.Data;
using PiholeReportServer.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Options ──────────────────────────────────────────────────────────────────
builder.Services.Configure<SqlOptions>(builder.Configuration.GetSection(SqlOptions.SectionName));
builder.Services.Configure<ReportingOptions>(builder.Configuration.GetSection(ReportingOptions.SectionName));
builder.Services.Configure<AiOptions>(builder.Configuration.GetSection(AiOptions.SectionName));

// ── Entra ID sign-in ─────────────────────────────────────────────────────────
// The default challenge scheme must be the OpenID Connect scheme that
// AddMicrosoftIdentityWebApp actually registers. Naming it "AzureAd" (the
// configuration section) instead leaves the challenge pointing at a scheme that
// was never registered, and every anonymous request fails with
// "No authenticationScheme was specified" rather than redirecting to sign-in.
//
// The SQL scope is requested up front ONLY when an Entra SQL mode is actually
// configured. Requesting it unconditionally breaks sign-in outright: Entra
// issues the authorization code and then refuses to redeem it with
//   AADSTS650057: Invalid resource. The client has requested access to a
//   resource which is not listed in the requested permissions...
// because the SqlLogin and Integrated modes never need an Azure SQL permission
// on the app registration. Token acquisition is still wired up in every mode,
// so ITokenAcquisition resolves and the Entra modes can fetch a token on
// demand; only the up-front scope request is conditional.
var sqlAuthMode = builder.Configuration.GetValue("Sql:AuthMode", SqlAuthMode.SqlLogin);
var sqlNeedsEntraToken = sqlAuthMode is SqlAuthMode.EntraApp or SqlAuthMode.EntraOnBehalfOf;
var sqlScope = builder.Configuration["Sql:TokenScope"]
               ?? "https://database.windows.net//.default";

var authentication = builder.Services
    .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

(sqlNeedsEntraToken
        ? authentication.EnableTokenAcquisitionToCallDownstreamApi([sqlScope])
        : authentication.EnableTokenAcquisitionToCallDownstreamApi())
    .AddInMemoryTokenCaches();

builder.Services.AddAuthorization(options =>
{
    // Anyone who signs in and holds the Viewer role may read reports. If no app
    // roles are assigned in Entra ID at all, fall back to "any authenticated
    // user" so a fresh install is usable before roles are wired up — see
    // docs/02-entra-id-setup.md for turning that off.
    var requireRoles = builder.Configuration.GetValue("AzureAd:RequireAppRoles", false);

    options.AddPolicy(AuthorizationPolicies.Viewer, policy =>
    {
        policy.RequireAuthenticatedUser();
        if (requireRoles)
        {
            policy.RequireRole(AppRoles.Viewer, AppRoles.SqlAuthor);
        }
    });

    options.AddPolicy(AuthorizationPolicies.SqlAuthor, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireRole(AppRoles.SqlAuthor);
    });

    // Every endpoint requires an authenticated user unless it opts out with
    // [AllowAnonymous]. This is the "whole website is secured by Entra ID"
    // requirement, enforced centrally rather than page by page.
    options.FallbackPolicy = options.GetPolicy(AuthorizationPolicies.Viewer);
});

builder.Services.AddRazorPages(options =>
    {
        options.Conventions.AuthorizeFolder("/", AuthorizationPolicies.Viewer);
    })
    .AddMvcOptions(options => options.Filters.Add(new AuthorizeFilter(AuthorizationPolicies.Viewer)))
    .AddMicrosoftIdentityUI();

// ── Application services ─────────────────────────────────────────────────────
builder.Services.AddScoped<ISqlConnectionFactory, SqlConnectionFactory>();
builder.Services.AddScoped<ReportRunner>();
builder.Services.AddSingleton<ReportCatalog>();
// Singleton: it holds materialised result sets across requests, with its own
// size-limited MemoryCache so large results cannot exhaust the worker.
builder.Services.AddSingleton<ResultCache>();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<ClientDirectory>();
builder.Services.AddScoped<AdlistDirectory>();
builder.Services.AddScoped<SavedReportStore>();
// Standing facts for the Analyst - which hostname "Jason's phone" means. Scoped
// like the other SQL-backed stores; the facts live in the database, not in memory,
// because being taught something once should survive a recycle.
builder.Services.AddScoped<AnalystMemoryStore>();
// Singleton: it remembers that the preferred inference host is unreachable, and
// that has to outlive one request or every request pays the connect timeout again.
builder.Services.AddSingleton<AiEndpointSelector>();
// Typed client so the long inference timeout is scoped to this one dependency
// rather than applied to every outbound call the app might make.
builder.Services.AddHttpClient<AiClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        // Bound the CONNECT separately from the overall timeout. The preferred host
        // is a laptop that leaves the network; an absent host can otherwise absorb
        // ~21s of SYN retries before failing, and that delay would be paid before
        // failover could even begin. Generation itself is unaffected - that is
        // governed by HttpClient.Timeout.
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    });
builder.Services.AddScoped<AiAgent>();
// Singleton, like ResultCache: an Analyst conversation has to outlive the request
// that added a turn to it, or every follow-up would start from nothing.
builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddScoped<NightlyFindingStore>();
// Hosted rather than a separate scheduled task: it reuses the same agent, guard and
// read-only login as the interactive features, so there is one code path to trust.
builder.Services.AddHostedService<NightlyAnalysisService>();

builder.Services.AddHealthChecks();

// Persist the data protection key ring outside the application directory when a
// path is configured. Without this, IIS regenerates the keys on every app pool
// recycle and redeploy, which invalidates the auth cookie and signs every user
// out. Left unset (development), the framework default applies.
var keyPath = builder.Configuration["DataProtection:KeyPath"];
if (!string.IsNullOrWhiteSpace(keyPath))
{
    builder.Services
        .AddDataProtection()
        .PersistKeysToFileSystem(Directory.CreateDirectory(keyPath))
        .SetApplicationName("PiholeReportServer");
}

// Behind IIS or another reverse proxy, honour the forwarded scheme so the
// OpenID Connect redirect_uri is generated as https and matches the app
// registration.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    h["X-Frame-Options"] = "DENY";
    await next();
});

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();
app.MapHealthChecks("/healthz").AllowAnonymous();

app.Run();
