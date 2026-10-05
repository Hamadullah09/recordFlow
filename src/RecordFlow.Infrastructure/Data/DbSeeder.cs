using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RecordFlow.Core;
using RecordFlow.Core.Entities;

namespace RecordFlow.Infrastructure.Data;

/// <summary>Idempotent seeding of roles, the first administrator and default configuration.</summary>
public static class DbSeeder
{
    public static async Task SeedAsync(IServiceProvider services, IConfiguration config, ILogger logger, CancellationToken ct = default)
    {
        var db = services.GetRequiredService<ApplicationDbContext>();
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();

        foreach (var role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole(role));
        }

        await SeedAdministratorAsync(userManager, config, logger);
        await SeedAdminColumnsAsync(db, ct);
        await SeedFormFieldsAsync(db, ct);
    }

    private static async Task SeedAdministratorAsync(UserManager<ApplicationUser> userManager, IConfiguration config, ILogger logger)
    {
        var userName = config["Seed:AdminUserId"];
        var email = config["Seed:AdminEmail"];
        var password = config["Seed:AdminPassword"];
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return;

        if (await userManager.FindByNameAsync(userName) is not null) return;

        var admin = new ApplicationUser
        {
            UserName = userName,
            Email = email,
            EmailConfirmed = true,
            FullName = config["Seed:AdminFullName"] ?? "Portal Administrator",
        };
        var result = await userManager.CreateAsync(admin, password);
        if (!result.Succeeded)
        {
            logger.LogError("Could not create the seed administrator: {Errors}", string.Join("; ", result.Errors.Select(e => e.Description)));
            return;
        }
        await userManager.AddToRolesAsync(admin, [Roles.Administrator, Roles.User]);
        logger.LogInformation("Seed administrator '{UserName}' created.", userName);
    }

    private static async Task SeedAdminColumnsAsync(ApplicationDbContext db, CancellationToken ct)
    {
        var existing = await db.AdminColumns.Select(c => c.Slot).ToListAsync(ct);
        var defaults = new[]
        {
            new AdminColumn { Slot = 1, Name = "store_name", DisplayLabel = "Store Name", IsActive = true, SourceField = "Store Name", DisplayOrder = 1 },
            new AdminColumn { Slot = 2, Name = "store_id", DisplayLabel = "Store ID", IsActive = true, SourceField = "Store ID", DisplayOrder = 2 },
            new AdminColumn { Slot = 3, Name = "zip_code", DisplayLabel = "ZIP Code", IsActive = true, SourceField = "ZIP Code", DisplayOrder = 3 },
            new AdminColumn { Slot = 4, Name = "region", DisplayLabel = "Region", IsActive = true, SourceField = "Region", DisplayOrder = 4 },
            new AdminColumn { Slot = 5, Name = "priority", DisplayLabel = "Priority", IsActive = false, FieldType = FieldType.Select, Options = "Standard, High, Urgent", AllowUserEdit = true, DisplayOrder = 5 },
            new AdminColumn { Slot = 6, Name = "column_6", DisplayLabel = "Column 6", IsActive = false, DisplayOrder = 6 },
        };
        foreach (var column in defaults.Where(d => !existing.Contains(d.Slot)))
        {
            column.UpdatedBy = "system";
            db.AdminColumns.Add(column);
        }
        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedFormFieldsAsync(ApplicationDbContext db, CancellationToken ct)
    {
        if (await db.FormFields.AnyAsync(ct)) return;

        var order = 0;
        FormFieldDefinition F(string key, string label, FormSection section, FieldType type, bool required, string aliases, string? help = null, int max = 250) =>
            new()
            {
                Key = key, Label = label, Section = section, FieldType = type, IsRequired = required,
                CsvAliases = aliases, HelpText = help, MaxLength = max, DisplayOrder = ++order * 10, RecipientEditable = true,
            };

        db.FormFields.AddRange(
            F("StoreId", "Store ID", FormSection.Store, FieldType.Text, false, "Store ID, StoreID, Store Number, Store No, Store #"),
            F("StoreName", "Store Name", FormSection.Store, FieldType.Text, true, "Store Name, Store, Business Name, Location Name"),
            F("AddressLine1", "Address Line 1", FormSection.Store, FieldType.Text, true, "Address Line 1, Address 1, Address, Street, Street Address"),
            F("AddressLine2", "Address Line 2", FormSection.Store, FieldType.Text, false, "Address Line 2, Address 2, Suite, Unit"),
            F("City", "City", FormSection.Store, FieldType.Text, true, "City, Town"),
            F("State", "State", FormSection.Store, FieldType.State, true, "State, State Code, ST"),
            F("ZipCode", "ZIP Code", FormSection.Store, FieldType.ZipCode, true, "ZIP Code, ZIP, Zipcode, Postal Code"),
            F("StorePhone", "Store Phone", FormSection.Store, FieldType.Phone, true, "Store Phone Number, Store Phone, Phone, Phone Number"),

            F("OwnerName", "Owner Name", FormSection.Owner, FieldType.Text, true, "Owner Name, Owner"),
            F("OwnerPhone", "Owner Phone", FormSection.Owner, FieldType.Phone, true, "Present Phone Number, Present Phone, Owner Phone, Owner Phone Number"),
            F("OwnerEmail", "Owner Email", FormSection.Owner, FieldType.Email, false, "Owner Email, Owner Email Address, Email"),
            F("EmergencyContactName", "Emergency Contact", FormSection.Owner, FieldType.Text, false, "Emergency Contact, Emergency Contact Name, Owner Emergency Contact"),
            F("EmergencyContactPhone", "Emergency Contact Phone", FormSection.Owner, FieldType.Phone, true, "Owner Emergency Contact Number, Emergency Contact Number, Emergency Contact Phone, Emergency Phone"),

            F("AlarmServiceId", "Alarm Service ID", FormSection.Alarm, FieldType.Text, false, "Alarm Service ID, Alarm ID, Alarm Account, Alarm Account Number"),
            F("AlarmCompany", "Alarm Company", FormSection.Alarm, FieldType.Text, false, "Alarm Company, Alarm Service Name, Alarm Provider"),
            F("AlarmServicePhone", "Alarm Service Phone", FormSection.Alarm, FieldType.Phone, false, "Alarm Service Phone Number, Alarm Service Phone, Alarm Phone"),

            F("FireStationNumber", "Fire Station Number", FormSection.Emergency, FieldType.Text, false, "Fire Station Number, Fire Station, Fire Station #, Fire Station No"),
            F("PoliceDepartmentPhone", "Police Department Phone", FormSection.Emergency, FieldType.Phone, false, "Police Department Phone, Police Phone, Police Non-Emergency"),

            F("Notes", "Notes", FormSection.Other, FieldType.TextArea, false, "Notes, Comments, Remarks", "Anything else we should know.", 2000));

        await db.SaveChangesAsync(ct);
    }
}
