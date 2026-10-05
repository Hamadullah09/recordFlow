using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Net.Http.Headers;
using RecordFlow.Core.Abstractions;

namespace RecordFlow.Web.Infrastructure;

/// <summary>Adds browser security headers to every response.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IWebHostEnvironment env)
{
    private readonly string _csp = string.Join("; ",
        "default-src 'self'",
        "base-uri 'self'",
        "object-src 'none'",
        "frame-ancestors 'none'",
        "img-src 'self' data:",
        "style-src 'self'",
        "script-src 'self'",
        "font-src 'self'",
        "connect-src 'self'",
        // Stripe Checkout is reached by redirect after a form post.
        "form-action 'self' https://checkout.stripe.com") + (env.IsDevelopment() ? "" : "; upgrade-insecure-requests");

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "same-origin";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            h[HeaderNames.ContentSecurityPolicy] = _csp;

            // Pages may contain personal data: never cache them. Static assets set their own caching.
            if (!h.ContainsKey(HeaderNames.CacheControl))
            {
                h.CacheControl = "no-store, max-age=0";
                h.Pragma = "no-cache";
            }

            // Shared-form links carry a secret token: keep it out of Referer headers and search engines.
            if (context.Request.Path.StartsWithSegments("/f"))
            {
                h["Referrer-Policy"] = "no-referrer";
                h["X-Robots-Tag"] = "noindex, nofollow";
            }
            return Task.CompletedTask;
        });
        return next(context);
    }
}

/// <summary>
/// Optional network restriction for the Admin Portal. When AdminPortal:AllowedIps is configured, requests
/// to /Admin from any other address receive 404 (the portal's existence is not revealed).
/// </summary>
public sealed class AdminPortalGuardMiddleware(RequestDelegate next, IConfiguration config)
{
    private readonly IPAddress[] _allowed = (config.GetSection("AdminPortal:AllowedIps").Get<string[]>() ?? [])
        .Select(s => IPAddress.TryParse(s, out var ip) ? ip : null).OfType<IPAddress>().ToArray();

    public Task InvokeAsync(HttpContext context)
    {
        if (_allowed.Length > 0 && context.Request.Path.StartsWithSegments("/Admin", StringComparison.OrdinalIgnoreCase))
        {
            var remote = context.Connection.RemoteIpAddress;
            if (remote is null || !_allowed.Any(a => a.Equals(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote)))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }
        }
        return next(context);
    }
}

/// <summary>
/// Turns workflow exceptions into friendly redirects: an expired workspace sends the user back to the
/// dashboard; a business-rule violation is shown as an error message.
/// </summary>
public sealed class WorkflowExceptionPageFilter(ITempDataDictionaryFactory tempDataFactory) : IAsyncPageFilter
{
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var executed = await next();
        if (executed.Exception is null || executed.ExceptionHandled) return;

        var tempData = tempDataFactory.GetTempData(context.HttpContext);
        switch (executed.Exception)
        {
            case WorkspaceExpiredException:
                tempData["Error"] = "Your working session has ended or expired, so its temporary data was removed. Upload your CSV again to continue.";
                executed.Result = new RedirectToPageResult("/Dashboard");
                executed.ExceptionHandled = true;
                break;
            case WorkflowException wf:
                tempData["Error"] = wf.Message;
                var request = context.HttpContext.Request;
                executed.Result = HttpMethods.IsGet(request.Method)
                    ? new RedirectToPageResult("/Dashboard")
                    : new RedirectResult(request.PathBase + request.Path + request.QueryString);
                executed.ExceptionHandled = true;
                break;
        }
    }
}
