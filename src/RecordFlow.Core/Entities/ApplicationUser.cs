using Microsoft.AspNetCore.Identity;

namespace RecordFlow.Core.Entities;

/// <summary>
/// Portal account. <see cref="IdentityUser.UserName"/> holds the sign-in "User ID";
/// <see cref="FullName"/> holds the display "User Name" captured at registration.
/// </summary>
public class ApplicationUser : IdentityUser
{
    [PersonalData]
    public string FullName { get; set; } = string.Empty;

    public int? CompanyId { get; set; }
    public Company? Company { get; set; }

    /// <summary>Set by an administrator. Disabled users cannot sign in.</summary>
    public bool IsDisabled { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAtUtc { get; set; }
}
