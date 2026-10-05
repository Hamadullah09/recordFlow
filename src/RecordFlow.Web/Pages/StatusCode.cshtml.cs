using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RecordFlow.Web.Pages;

[IgnoreAntiforgeryToken]
public class StatusCodeModel : PageModel
{
    public int Code { get; private set; }
    public string Title { get; private set; } = "Page not found";
    public string Message { get; private set; } = "The page you're looking for doesn't exist or is no longer available.";

    public void OnGet(int code = 404) => Set(code);
    public void OnPost(int code = 404) => Set(code);

    private void Set(int code)
    {
        Code = code;
        (Title, Message) = code switch
        {
            400 => ("Bad request", "The request could not be processed. Please refresh the page and try again."),
            403 => ("Access denied", "You don't have permission to view this page."),
            404 => ("Page not found", "The page you're looking for doesn't exist or is no longer available."),
            413 => ("File too large", "The file you uploaded is larger than the allowed size."),
            429 => ("Too many requests", "You've made too many requests in a short time. Please wait a minute and try again."),
            _ => ("Something went wrong", "We couldn't complete your request. Please try again."),
        };
    }
}
