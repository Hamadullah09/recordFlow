using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Entities;

namespace RecordFlow.Web.Infrastructure;

public static class Policies
{
    public const string PortalUser = "PortalUser";
    public const string AdminOnly = "AdminOnly";
}

public static class RateLimits
{
    public const string Auth = "auth";
    public const string Upload = "upload";
    public const string Share = "share";
    public const string Recipient = "recipient";
    public const string Api = "api";
}

public static class AppClaims
{
    public const string FullName = "rf:full_name";
}

public static class ClaimsPrincipalExtensions
{
    public static string GetUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("User is not signed in.");

    public static string GetDisplayName(this ClaimsPrincipal user) =>
        user.FindFirstValue(AppClaims.FullName) is { Length: > 0 } name ? name : user.Identity?.Name ?? "User";
}

/// <summary>Adds the user's display name to the auth cookie so layouts don't need a database lookup.</summary>
public sealed class AppClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>(userManager, roleManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(AppClaims.FullName, user.FullName));
        return identity;
    }
}

/// <summary>Base class for signed-in portal pages.</summary>
public abstract class PortalPageModel : PageModel
{
    protected string UserId => User.GetUserId();
    protected string DisplayName => User.GetDisplayName();

    protected void FlashSuccess(string message) => TempData["Success"] = message;
    protected void FlashError(string message) => TempData["Error"] = message;
    protected void FlashInfo(string message) => TempData["Info"] = message;
}

/// <summary>Builds absolute links from the configured public URL (never from the request Host header in production).</summary>
public sealed class LinkBuilder(IHttpContextAccessor http, IOptions<AppOptions> app, IWebHostEnvironment env)
{
    public string BaseUrl
    {
        get
        {
            var configured = app.Value.PublicBaseUrl;
            if (!string.IsNullOrWhiteSpace(configured)) return configured.TrimEnd('/');
            if (!env.IsDevelopment()) throw new InvalidOperationException("App:PublicBaseUrl is not configured.");
            var req = http.HttpContext?.Request ?? throw new InvalidOperationException("No active request.");
            return $"{req.Scheme}://{req.Host}{req.PathBase}";
        }
    }

    public string Absolute(string path) => BaseUrl + (path.StartsWith('/') ? path : "/" + path);
    public string ShareUrl(string token) => Absolute("/f/" + Uri.EscapeDataString(token));
    public string RecordVerifyUrl(string recordKey) => Absolute($"/records/{Uri.EscapeDataString(recordKey)}/verify");
}

/// <summary>Formats UTC timestamps in the portal's U.S. time zone.</summary>
public sealed class TimeDisplay
{
    private static readonly Dictionary<string, string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["America/New_York"] = "ET", ["Eastern Standard Time"] = "ET",
        ["America/Chicago"] = "CT", ["Central Standard Time"] = "CT",
        ["America/Denver"] = "MT", ["Mountain Standard Time"] = "MT",
        ["America/Phoenix"] = "MST", ["US Mountain Standard Time"] = "MST",
        ["America/Los_Angeles"] = "PT", ["Pacific Standard Time"] = "PT",
        ["America/Anchorage"] = "AKT", ["Alaskan Standard Time"] = "AKT",
        ["Pacific/Honolulu"] = "HT", ["Hawaiian Standard Time"] = "HT",
    };

    private readonly TimeZoneInfo _zone;

    public TimeDisplay(IOptions<AppOptions> options)
    {
        var id = options.Value.TimeZone;
        _zone = TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz) ? tz : TimeZoneInfo.Utc;
        Abbreviation = Abbreviations.GetValueOrDefault(id) ?? (_zone == TimeZoneInfo.Utc ? "UTC" : "");
    }

    public string Abbreviation { get; }

    public DateTimeOffset ToLocal(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, _zone);
    public DateTimeOffset ToLocal(DateTime utc) => ToLocal(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)));

    public string Date(DateTimeOffset? utc) => utc is null ? "—" : ToLocal(utc.Value).ToString("MM/dd/yyyy");
    public string Time(DateTimeOffset? utc) => utc is null ? "—" : $"{ToLocal(utc.Value):h:mm tt} {Abbreviation}".TrimEnd();
    public string DateAndTime(DateTimeOffset? utc) => utc is null ? "—" : $"{ToLocal(utc.Value):MMM d, yyyy h:mm tt} {Abbreviation}".TrimEnd();

    public string Date(DateTime? utc) => utc is null ? "—" : Date(ToLocal(utc.Value));
    public string Time(DateTime? utc) => utc is null ? "—" : Time(ToLocal(utc.Value));
    public string DateAndTime(DateTime? utc) => utc is null ? "—" : DateAndTime(ToLocal(utc.Value));
}
