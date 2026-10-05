using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using RecordFlow.Core;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Entities;
using RecordFlow.Infrastructure.Data;
using RecordFlow.Web.Infrastructure;

namespace RecordFlow.Web.Pages.Account;

[EnableRateLimiting(RateLimits.Auth)]
public class RegisterModel(
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn,
    ApplicationDbContext db,
    AccountEmails accountEmails,
    IAuditLogger audit) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();

    public sealed class InputModel
    {
        [Required, StringLength(100, MinimumLength = 2), Display(Name = "User Name")]
        public string FullName { get; set; } = string.Empty;

        [Required, StringLength(32, MinimumLength = 4), Display(Name = "User ID")]
        [RegularExpression(@"^[A-Za-z0-9._-]+$", ErrorMessage = "User ID may contain only letters, numbers, periods, hyphens and underscores.")]
        public string UserId { get; set; } = string.Empty;

        [Required, EmailAddress, StringLength(254), Display(Name = "Email address")]
        public string Email { get; set; } = string.Empty;

        [Required, StringLength(200, MinimumLength = 2), Display(Name = "Company name")]
        public string CompanyName { get; set; } = string.Empty;

        [Required, StringLength(128, MinimumLength = 10, ErrorMessage = "Password must be at least 10 characters."), DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [Required, DataType(DataType.Password), Display(Name = "Confirm password")]
        [Compare(nameof(Password), ErrorMessage = "Passwords do not match.")]
        public string ConfirmPassword { get; set; } = string.Empty;

        [Display(Name = "I agree to the Terms of Service and Privacy Notice")]
        public bool AcceptTerms { get; set; }
    }

    public IActionResult OnGet() =>
        User.Identity?.IsAuthenticated == true ? RedirectToPage("/Dashboard") : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        Input.FullName = Input.FullName.Trim();
        Input.UserId = Input.UserId.Trim();
        Input.Email = Input.Email.Trim();
        Input.CompanyName = Input.CompanyName.Trim();
        if (!Input.AcceptTerms)
            ModelState.AddModelError("Input.AcceptTerms", "Please accept the Terms of Service to continue.");
        if (!ModelState.IsValid) return Page();

        if (await users.FindByNameAsync(Input.UserId) is not null)
            ModelState.AddModelError("Input.UserId", "That User ID is already taken. Please choose another.");
        if (await users.FindByEmailAsync(Input.Email) is not null)
            ModelState.AddModelError("Input.Email", "An account with this email already exists. Try signing in or resetting your password.");
        if (!ModelState.IsValid) return Page();

        var user = new ApplicationUser
        {
            UserName = Input.UserId,
            Email = Input.Email,
            FullName = Input.FullName,
        };
        var result = await users.CreateAsync(user, Input.Password);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                var key = error.Code switch
                {
                    var c when c.StartsWith("Password", StringComparison.Ordinal) => "Input.Password",
                    "DuplicateUserName" or "InvalidUserName" => "Input.UserId",
                    "DuplicateEmail" or "InvalidEmail" => "Input.Email",
                    _ => string.Empty,
                };
                ModelState.AddModelError(key, error.Description);
            }
            return Page();
        }

        user.CompanyId = (await FindOrCreateCompanyAsync(Input.CompanyName, ct)).Id;
        await users.UpdateAsync(user);
        await users.AddToRoleAsync(user, Roles.User);
        await audit.LogAsync(AuditCategories.Security, "UserRegistered", null, nameof(ApplicationUser), user.UserName, userId: user.Id, userName: user.UserName, ct: ct);

        if (users.Options.SignIn.RequireConfirmedEmail)
        {
            try
            {
                await accountEmails.SendConfirmationAsync(user, ct);
            }
            catch (InvalidOperationException)
            {
                TempData["Error"] = "Your account was created, but we couldn't send the confirmation email. Use “Resend confirmation” on the sign-in page.";
            }
            return RedirectToPage("./CheckEmail", new { reason = "confirm" });
        }

        await signIn.SignInAsync(user, isPersistent: false);
        TempData["Success"] = "Welcome! Your account is ready.";
        return RedirectToPage("/Dashboard");
    }

    private async Task<Company> FindOrCreateCompanyAsync(string name, CancellationToken ct)
    {
        var normalized = Company.Normalize(name);
        var company = await db.Companies.FirstOrDefaultAsync(c => c.NormalizedName == normalized, ct);
        if (company is not null) return company;

        company = new Company { Name = name, NormalizedName = normalized };
        db.Companies.Add(company);
        try
        {
            await db.SaveChangesAsync(ct);
            return company;
        }
        catch (DbUpdateException)
        {
            // Another registration created the same company at the same moment.
            db.Entry(company).State = EntityState.Detached;
            return await db.Companies.FirstAsync(c => c.NormalizedName == normalized, ct);
        }
    }
}
