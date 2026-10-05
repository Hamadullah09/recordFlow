using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using RecordFlow.Core;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Entities;
using RecordFlow.Infrastructure;
using RecordFlow.Infrastructure.Data;
using RecordFlow.Web.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
var env = builder.Environment;

// ───────────── Core services ─────────────
builder.Services.AddInfrastructure(config, env);
builder.Services.AddScoped<LinkBuilder>();
builder.Services.AddScoped<AccountEmails>();
builder.Services.AddSingleton<TimeDisplay>();

// ───────────── Identity ─────────────
builder.Services
    .AddIdentity<ApplicationUser, IdentityRole>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._";
        o.Password.RequiredLength = 10;
        o.Password.RequireDigit = true;
        o.Password.RequireLowercase = true;
        o.Password.RequireUppercase = true;
        o.Password.RequireNonAlphanumeric = true;
        o.Password.RequiredUniqueChars = 4;
        o.Lockout.AllowedForNewUsers = true;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        o.SignIn.RequireConfirmedEmail = config.GetValue("Identity:RequireConfirmedEmail", true);
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders()
    .AddClaimsPrincipalFactory<AppClaimsPrincipalFactory>();

builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(3));
// Disabled users and changed passwords take effect on existing sessions within 5 minutes.
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(5));

builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "RecordFlow.Auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.ExpireTimeSpan = TimeSpan.FromMinutes(config.GetValue("Identity:SessionTimeoutMinutes", 60));
    o.SlidingExpiration = true;
    o.LoginPath = "/Account/Login";
    o.LogoutPath = "/Account/Logout";
    o.AccessDeniedPath = "/Account/AccessDenied";
    o.Events.OnRedirectToLogin = ctx => ApiAwareRedirect(ctx, StatusCodes.Status401Unauthorized);
    o.Events.OnRedirectToAccessDenied = ctx => ApiAwareRedirect(ctx, StatusCodes.Status403Forbidden);
});

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.PortalUser, p => p.RequireRole(Roles.User, Roles.Administrator))
    .AddPolicy(Policies.AdminOnly, p => p.RequireRole(Roles.Administrator));

// ───────────── MVC / Razor Pages ─────────────
builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/", Policies.PortalUser);
    foreach (var page in new[] { "/Index", "/Error", "/StatusCode", "/Privacy", "/Terms" })
        o.Conventions.AllowAnonymousToPage(page);
    foreach (var page in new[] { "Login", "Logout", "Register", "CheckEmail", "ConfirmEmail", "ResendConfirmation", "ForgotPassword", "ResetPassword", "Lockout", "AccessDenied" })
        o.Conventions.AllowAnonymousToPage("/Account/" + page);
    o.Conventions.AllowAnonymousToFolder("/Recipient");
    o.Conventions.AllowAnonymousToFolder("/Dev");
    o.Conventions.AuthorizeAreaFolder("Admin", "/", Policies.AdminOnly);
})
.AddMvcOptions(o => o.Filters.Add<WorkflowExceptionPageFilter>());

builder.Services.AddControllers();
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "RequestVerificationToken";
    o.Cookie.Name = "RecordFlow.Csrf";
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});
builder.Services.AddProblemDetails();

// Uploads: keep CSV bytes in memory (never buffered to a temp file) and cap the request size.
var maxUpload = config.GetValue<long>("CsvImport:MaxFileBytes", 5 * 1024 * 1024);
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = maxUpload + 64 * 1024;
    o.MemoryBufferThreshold = (int)Math.Min(int.MaxValue, maxUpload + 64 * 1024);
});

// ───────────── Rate limiting ─────────────
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(RateLimits.Auth, ctx => RateLimitPartition.GetFixedWindowLimiter(ClientIp(ctx),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy(RateLimits.Upload, ctx => RateLimitPartition.GetFixedWindowLimiter(UserOrIp(ctx),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(5) }));
    o.AddPolicy(RateLimits.Share, ctx => RateLimitPartition.GetFixedWindowLimiter(UserOrIp(ctx),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(10) }));
    o.AddPolicy(RateLimits.Recipient, ctx => RateLimitPartition.GetFixedWindowLimiter(ClientIp(ctx),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy(RateLimits.Api, ctx => RateLimitPartition.GetFixedWindowLimiter(UserOrIp(ctx),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddHealthChecks().AddDbContextCheck<ApplicationDbContext>("database");

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Trust proxies listed in configuration only (e.g. Azure Front Door / load balancer).
    foreach (var proxy in config.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [])
        if (System.Net.IPAddress.TryParse(proxy, out var ip)) o.KnownProxies.Add(ip);
});

builder.Services.AddHsts(o =>
{
    o.MaxAge = TimeSpan.FromDays(365);
    o.IncludeSubDomains = true;
});

if (!env.IsDevelopment())
{
    var publicUrl = config["App:PublicBaseUrl"];
    if (string.IsNullOrWhiteSpace(publicUrl) || !publicUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("App:PublicBaseUrl must be set to the site's https:// address in production.");
}

var app = builder.Build();

// ───────────── Database migration & seeding ─────────────
if (config.GetValue("Database:MigrateOnStartup", env.IsDevelopment()))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await db.Database.MigrateAsync();
    await DbSeeder.SeedAsync(scope.ServiceProvider, config, app.Logger);
}

// ───────────── HTTP pipeline ─────────────
var usCulture = new CultureInfo("en-US");
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture(usCulture),
    SupportedCultures = [usCulture],
    SupportedUICultures = [usCulture],
});

app.UseForwardedHeaders();
if (env.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/StatusCode", "?code={0}");
app.UseHttpsRedirection();
app.UseMiddleware<SecurityHeadersMiddleware>();

app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<AdminPortalGuardMiddleware>();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapControllers();
app.MapHealthChecks("/health").DisableHttpMetrics();

app.Run();

static Task ApiAwareRedirect(Microsoft.AspNetCore.Authentication.RedirectContext<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions> ctx, int statusCode)
{
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        ctx.Response.StatusCode = statusCode;
        return Task.CompletedTask;
    }
    ctx.Response.Redirect(ctx.RedirectUri);
    return Task.CompletedTask;
}

static string ClientIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

static string UserOrIp(HttpContext ctx) =>
    ctx.User.Identity?.IsAuthenticated == true ? "u:" + ctx.User.Identity.Name : "ip:" + ClientIp(ctx);

public partial class Program;
