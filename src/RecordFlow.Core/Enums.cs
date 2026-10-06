using System.ComponentModel.DataAnnotations;

namespace RecordFlow.Core;

public static class Roles
{
    public const string User = "User";
    public const string Administrator = "Administrator";

    public static readonly string[] All = [User, Administrator];
}

/// <summary>Input type used for form fields and admin-managed dashboard columns.</summary>
public enum FieldType
{
    Text = 0,
    Phone = 1,
    Email = 2,
    [Display(Name = "ZIP code")] ZipCode = 3,
    State = 4,
    [Display(Name = "Multi-line text")] TextArea = 5,
    Number = 6,
    Date = 7,
    [Display(Name = "Drop-down list")] Select = 8,
    [Display(Name = "Yes / No")] YesNo = 9,
}

public enum FormSection
{
    [Display(Name = "Store Information")] Store = 0,
    [Display(Name = "Owner Information")] Owner = 1,
    [Display(Name = "Alarm Information")] Alarm = 2,
    [Display(Name = "Emergency Information")] Emergency = 3,
    [Display(Name = "Other Information")] Other = 4,
    [Display(Name = "Additional Information")] Additional = 5,
    [Display(Name = "Billing & Payment Details")] Billing = 6,
}

public enum OrderStatus
{
    Created = 0,
    [Display(Name = "Awaiting payment")] Pending = 1,
    Processing = 2,
    Paid = 3,
    Failed = 4,
    Canceled = 5,
    Refunded = 6,
}

/// <summary>
/// Lifecycle of a temporary working record (lives only in the session workspace):
/// form → share → recipient completes → verify &amp; confirm → payment → completed.
/// </summary>
public enum RecordStatus
{
    [Display(Name = "Not started")] Imported = 0,
    [Display(Name = "Form ready")] FormGenerated = 1,
    [Display(Name = "Waiting for recipient")] SharedPending = 2,
    [Display(Name = "Ready for verification")] ReadyForVerification = 3,
    [Display(Name = "Awaiting payment")] AwaitingPayment = 4,
    [Display(Name = "Completed")] Verified = 5,
}

/// <summary>Result of one call, chosen by the caller.</summary>
public enum CallOutcome
{
    [Display(Name = "Interested")] Interested = 0,
    [Display(Name = "Not interested")] NotInterested = 1,
    [Display(Name = "Call back")] CallBack = 2,
    [Display(Name = "No answer")] NoAnswer = 3,
    [Display(Name = "Left voicemail")] Voicemail = 4,
    [Display(Name = "Wrong number")] WrongNumber = 5,
}

/// <summary>Where the current value of a form field came from.</summary>
public enum FieldSource
{
    Empty = 0,
    Csv = 1,
    User = 2,
    Recipient = 3,
}

public enum PaymentMethodKind
{
    [Display(Name = "Credit or debit card")] Card = 0,
    [Display(Name = "U.S. bank account (ACH)")] BankAccount = 1,
}

public static class AuditCategories
{
    public const string Security = "Security";
    public const string Admin = "Admin";
    public const string Record = "Record";
    public const string Payment = "Payment";
    public const string Workspace = "Workspace";
}
