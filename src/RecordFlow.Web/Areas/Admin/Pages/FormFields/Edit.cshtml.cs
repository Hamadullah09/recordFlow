using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RecordFlow.Core;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Entities;
using RecordFlow.Core.Services;
using RecordFlow.Infrastructure.Data;
using RecordFlow.Infrastructure.Services;

namespace RecordFlow.Web.Areas.Admin.Pages.FormFields;

/// <summary>Create or edit a form field definition. Changes apply to forms generated afterwards.</summary>
public class EditModel(ApplicationDbContext db, PortalConfigService config, IAuditLogger audit) : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public int? Id { get; set; }
    [BindProperty] public InputModel Input { get; set; } = new();

    public sealed class InputModel
    {
        [Required, StringLength(100), RegularExpression(@"^[A-Za-z][A-Za-z0-9]*$", ErrorMessage = "Use letters and numbers only, starting with a letter (e.g. StoreManager).")]
        public string Key { get; set; } = string.Empty;

        [Required, StringLength(150)] public string Label { get; set; } = string.Empty;
        public FormSection Section { get; set; } = FormSection.Other;
        [Display(Name = "Field type")] public FieldType FieldType { get; set; } = FieldType.Text;
        [Display(Name = "Required")] public bool IsRequired { get; set; }
        [Display(Name = "Active")] public bool IsActive { get; set; } = true;
        [Display(Name = "Recipient can edit")] public bool RecipientEditable { get; set; } = true;
        [Range(0, 9999), Display(Name = "Display order")] public int DisplayOrder { get; set; } = 100;
        [StringLength(1000), Display(Name = "CSV column aliases (comma-separated)")] public string? CsvAliases { get; set; }
        [StringLength(300), Display(Name = "Help text")] public string? HelpText { get; set; }
        [Range(1, 4000), Display(Name = "Maximum length")] public int MaxLength { get; set; } = 250;
        [StringLength(2000), Display(Name = "Options (comma-separated)")] public string? Options { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (Id is int id)
        {
            var f = await db.FormFields.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (f is null) return NotFound();
            Input = new InputModel
            {
                Key = f.Key, Label = f.Label, Section = f.Section, FieldType = f.FieldType, IsRequired = f.IsRequired,
                IsActive = f.IsActive, RecipientEditable = f.RecipientEditable, DisplayOrder = f.DisplayOrder,
                CsvAliases = f.CsvAliases, HelpText = f.HelpText, MaxLength = f.MaxLength, Options = f.Options,
            };
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        FormFieldDefinition? field = null;
        if (Id is int id)
        {
            field = await db.FormFields.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (field is null) return NotFound();
            Input.Key = field.Key; // keys are immutable once created
            ModelState.Remove("Input.Key");
        }
        else if (await db.FormFields.AnyAsync(x => x.Key == Input.Key, ct))
        {
            ModelState.AddModelError("Input.Key", "A field with this key already exists.");
        }
        if (Input.Key.StartsWith(FormBuilder.AdditionalFieldPrefix, StringComparison.OrdinalIgnoreCase))
            ModelState.AddModelError("Input.Key", "This prefix is reserved.");
        if (Input.FieldType == FieldType.Select && OptionParser.Parse(Input.Options).Count == 0)
            ModelState.AddModelError("Input.Options", "Drop-down fields need at least one option.");
        if (!ModelState.IsValid) return Page();

        field ??= db.FormFields.Add(new FormFieldDefinition { Key = Input.Key }).Entity;
        field.Label = Input.Label.Trim();
        field.Section = Input.Section;
        field.FieldType = Input.FieldType;
        field.IsRequired = Input.IsRequired;
        field.IsActive = Input.IsActive;
        field.RecipientEditable = Input.RecipientEditable;
        field.DisplayOrder = Input.DisplayOrder;
        field.CsvAliases = string.IsNullOrWhiteSpace(Input.CsvAliases) ? null : string.Join(", ", OptionParser.Parse(Input.CsvAliases));
        field.HelpText = string.IsNullOrWhiteSpace(Input.HelpText) ? null : Input.HelpText.Trim();
        field.MaxLength = Input.MaxLength;
        field.Options = Input.FieldType == FieldType.Select ? string.Join(", ", OptionParser.Parse(Input.Options)) : null;
        await db.SaveChangesAsync(ct);
        config.Invalidate();

        await audit.LogAsync(AuditCategories.Admin, Id is null ? "FormFieldCreated" : "FormFieldUpdated",
            $"{field.Label} ({field.FieldType}, required={field.IsRequired}, active={field.IsActive})", nameof(FormFieldDefinition), field.Key, ct: ct);
        FlashSuccess("Field saved. It applies to forms generated from now on.");
        return RedirectToPage("./Index");
    }
}
