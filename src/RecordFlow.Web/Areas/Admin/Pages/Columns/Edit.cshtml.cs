using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RecordFlow.Core;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Entities;
using RecordFlow.Infrastructure.Data;
using RecordFlow.Infrastructure.Services;

namespace RecordFlow.Web.Areas.Admin.Pages.Columns;

public class EditModel(ApplicationDbContext db, PortalConfigService config, IAuditLogger audit, TimeProvider clock) : AdminPageModel
{
    private static readonly FieldType[] AllowedTypes = [FieldType.Text, FieldType.Number, FieldType.Date, FieldType.Select, FieldType.YesNo, FieldType.Phone, FieldType.Email, FieldType.ZipCode, FieldType.State];

    [BindProperty(SupportsGet = true)] public int Slot { get; set; }
    [BindProperty] public InputModel Input { get; set; } = new();
    public IReadOnlyList<string> KnownFields { get; private set; } = [];
    public IReadOnlyList<FieldType> Types => AllowedTypes;

    public sealed class InputModel
    {
        [Required, StringLength(100), RegularExpression(@"^[a-z][a-z0-9_]*$", ErrorMessage = "Use lowercase letters, numbers and underscores, starting with a letter.")]
        public string Name { get; set; } = string.Empty;

        [Required, StringLength(100), Display(Name = "Display label")] public string DisplayLabel { get; set; } = string.Empty;
        [Display(Name = "Field type")] public FieldType FieldType { get; set; }
        [Display(Name = "Active")] public bool IsActive { get; set; }
        [Display(Name = "Visible to users")] public bool IsVisible { get; set; } = true;
        [Display(Name = "Required before checkout")] public bool IsRequired { get; set; }
        [Display(Name = "Users can enter / edit values")] public bool AllowUserEdit { get; set; }
        [Range(0, 999), Display(Name = "Display order")] public int DisplayOrder { get; set; }
        [StringLength(200), Display(Name = "Value source (CSV column or form field)")] public string? SourceField { get; set; }
        [StringLength(500), Display(Name = "Default value")] public string? DefaultValue { get; set; }
        [StringLength(2000), Display(Name = "Options (comma-separated)")] public string? Options { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var column = await db.AdminColumns.AsNoTracking().FirstOrDefaultAsync(c => c.Slot == Slot, ct);
        if (column is null) return NotFound();
        Input = new InputModel
        {
            Name = column.Name, DisplayLabel = column.DisplayLabel, FieldType = column.FieldType, IsActive = column.IsActive,
            IsVisible = column.IsVisible, IsRequired = column.IsRequired, AllowUserEdit = column.AllowUserEdit,
            DisplayOrder = column.DisplayOrder, SourceField = column.SourceField, DefaultValue = column.DefaultValue, Options = column.Options,
        };
        await LoadKnownFieldsAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var column = await db.AdminColumns.FirstOrDefaultAsync(c => c.Slot == Slot, ct);
        if (column is null) return NotFound();

        if (!AllowedTypes.Contains(Input.FieldType)) ModelState.AddModelError("Input.FieldType", "Select a supported field type.");
        if (Input.FieldType == FieldType.Select && OptionParser.Parse(Input.Options).Count == 0)
            ModelState.AddModelError("Input.Options", "Drop-down columns need at least one option.");
        if (Input.IsRequired && !Input.AllowUserEdit && string.IsNullOrWhiteSpace(Input.SourceField) && string.IsNullOrWhiteSpace(Input.DefaultValue))
            ModelState.AddModelError("Input.IsRequired", "A required column needs a value source, a default value, or must be editable by users.");
        if (await db.AdminColumns.AnyAsync(c => c.Name == Input.Name && c.Slot != Slot, ct))
            ModelState.AddModelError("Input.Name", "Another column already uses this name.");
        if (!ModelState.IsValid)
        {
            await LoadKnownFieldsAsync(ct);
            return Page();
        }

        var before = $"{column.DisplayLabel}/{column.FieldType}/active={column.IsActive}/visible={column.IsVisible}/required={column.IsRequired}";
        column.Name = Input.Name;
        column.DisplayLabel = Input.DisplayLabel.Trim();
        column.FieldType = Input.FieldType;
        column.IsActive = Input.IsActive;
        column.IsVisible = Input.IsVisible;
        column.IsRequired = Input.IsRequired;
        column.AllowUserEdit = Input.AllowUserEdit;
        column.DisplayOrder = Input.DisplayOrder;
        column.SourceField = string.IsNullOrWhiteSpace(Input.SourceField) ? null : Input.SourceField.Trim();
        column.DefaultValue = string.IsNullOrWhiteSpace(Input.DefaultValue) ? null : Input.DefaultValue.Trim();
        column.Options = Input.FieldType == FieldType.Select ? string.Join(", ", OptionParser.Parse(Input.Options)) : null;
        column.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        column.UpdatedBy = AdminName;
        await db.SaveChangesAsync(ct);
        config.Invalidate();

        var after = $"{column.DisplayLabel}/{column.FieldType}/active={column.IsActive}/visible={column.IsVisible}/required={column.IsRequired}";
        await audit.LogAsync(AuditCategories.Admin, "AdminColumnUpdated", $"Slot {Slot}: {before} → {after}", nameof(AdminColumn), Slot.ToString(), ct: ct);
        FlashSuccess($"Column {Slot} saved. Users see the change on their next page load.");
        return RedirectToPage("./Index");
    }

    private async Task LoadKnownFieldsAsync(CancellationToken ct) =>
        KnownFields = await db.FormFields.AsNoTracking().Where(f => f.IsActive).OrderBy(f => f.Label).Select(f => f.Label).ToListAsync(ct);
}
