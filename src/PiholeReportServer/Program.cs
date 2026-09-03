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

// ── Entra ID sign-in ─────────────────────────────────────────────────────────
// AddMicrosoftIdentityWebApp wires up the OpenID Connect code flow against the
// tenant in configuration. EnableTokenAcquisitionToCallDownstreamApi is what
// makes an on-behalf-of token for SQL Server available later; the distributed
// (here in-memory) token cache holds the refresh material per session.
// The default challenge scheme must be the OpenID Connect scheme that
// AddMicrosoftIdentityWebApp actually registers. Naming it "AzureAd" (the
// configuration section) instead leaves the challenge pointing at a scheme that
// was never registered, and every anonymous request fails with
// "No authenticationScheme was specified" rather than redirecting to sign-in.
builder.Services
    .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"))
    .EnableTokenAcquisitionToCallDownstreamApi(
        [builder.Configuration["Sql:TokenScope"] ?? "https://database.windows.net//.default"])
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

builder.Services.AddHealthChecks();

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
