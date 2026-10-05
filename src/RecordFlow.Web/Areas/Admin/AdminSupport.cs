using Microsoft.AspNetCore.Mvc.RazorPages;
using RecordFlow.Core.Abstractions;
using RecordFlow.Web.Infrastructure;

namespace RecordFlow.Web.Areas.Admin;

/// <summary>Base class for Admin Portal pages (authorization is applied to the whole area by convention).</summary>
public abstract class AdminPageModel : PageModel
{
    protected string AdminId => User.GetUserId();
    protected string AdminName => User.Identity?.Name ?? "admin";

    protected void FlashSuccess(string message) => TempData["Success"] = message;
    protected void FlashError(string message) => TempData["Error"] = message;
}

public sealed record PagerModel(int Page, int TotalPages, int TotalCount, IDictionary<string, string?> RouteValues);

public static class Paging
{
    public static (int Page, int TotalPages) Normalize(int page, int totalCount, int pageSize)
    {
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        return (Math.Clamp(page, 1, totalPages), totalPages);
    }
}
