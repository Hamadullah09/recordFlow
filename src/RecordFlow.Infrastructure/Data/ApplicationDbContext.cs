using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using RecordFlow.Core.Entities;

namespace RecordFlow.Infrastructure.Data;

/// <summary>
/// Permanent application database. Holds accounts, configuration, orders, finalized business
/// records, call logs and audit logs. Uploaded CSV files and in-progress working data never enter this context.
/// </summary>
public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options), IDataProtectionKeyContext
{
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<AdminColumn> AdminColumns => Set<AdminColumn>();
    public DbSet<FormFieldDefinition> FormFields => Set<FormFieldDefinition>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<FinalizedRecord> FinalizedRecords => Set<FinalizedRecord>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<CallLog> CallLogs => Set<CallLog>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(e =>
        {
            e.Property(u => u.FullName).HasMaxLength(100).IsRequired();
            e.HasOne(u => u.Company).WithMany(c => c.Users).HasForeignKey(u => u.CompanyId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<Company>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(200).IsRequired();
            e.Property(c => c.NormalizedName).HasMaxLength(200).IsRequired();
            e.HasIndex(c => c.NormalizedName).IsUnique();
            e.Property(c => c.Phone).HasMaxLength(30);
            e.Property(c => c.AddressLine1).HasMaxLength(200);
            e.Property(c => c.AddressLine2).HasMaxLength(200);
            e.Property(c => c.City).HasMaxLength(100);
            e.Property(c => c.State).HasMaxLength(2);
            e.Property(c => c.ZipCode).HasMaxLength(10);
        });

        builder.Entity<AdminColumn>(e =>
        {
            e.HasIndex(c => c.Slot).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("CK_AdminColumns_Slot", "[Slot] BETWEEN 1 AND 6"));
            e.Property(c => c.Name).HasMaxLength(100).IsRequired();
            e.Property(c => c.DisplayLabel).HasMaxLength(100).IsRequired();
            e.Property(c => c.FieldType).HasConversion<string>().HasMaxLength(20);
            e.Property(c => c.SourceField).HasMaxLength(200);
            e.Property(c => c.DefaultValue).HasMaxLength(500);
            e.Property(c => c.Options).HasMaxLength(2000);
            e.Property(c => c.UpdatedBy).HasMaxLength(256);
        });

        builder.Entity<FormFieldDefinition>(e =>
        {
            e.ToTable("FormFields");
            e.HasIndex(f => f.Key).IsUnique();
            e.Property(f => f.Key).HasMaxLength(100).IsRequired();
            e.Property(f => f.Label).HasMaxLength(150).IsRequired();
            e.Property(f => f.Section).HasConversion<string>().HasMaxLength(20);
            e.Property(f => f.FieldType).HasConversion<string>().HasMaxLength(20);
            e.Property(f => f.CsvAliases).HasMaxLength(1000);
            e.Property(f => f.HelpText).HasMaxLength(300);
            e.Property(f => f.Options).HasMaxLength(2000);
        });

        builder.Entity<Order>(e =>
        {
            e.HasIndex(o => o.PublicId).IsUnique();
            e.HasIndex(o => o.OrderNumber).IsUnique();
            e.HasIndex(o => o.ProviderSessionId);
            e.HasIndex(o => new { o.UserId, o.CreatedAtUtc });
            e.HasIndex(o => o.Status);
            e.Property(o => o.OrderNumber).HasMaxLength(32).IsRequired();
            e.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(o => o.PaymentMethod).HasConversion<string>().HasMaxLength(20);
            e.Property(o => o.ServiceName).HasMaxLength(200).IsRequired();
            e.Property(o => o.ServiceDescription).HasMaxLength(500);
            e.Property(o => o.Subtotal).HasPrecision(18, 2);
            e.Property(o => o.Tax).HasPrecision(18, 2);
            e.Property(o => o.Fee).HasPrecision(18, 2);
            e.Property(o => o.Total).HasPrecision(18, 2);
            e.Property(o => o.Currency).HasMaxLength(3);
            e.Property(o => o.BillingName).HasMaxLength(100).IsRequired();
            e.Property(o => o.BillingEmail).HasMaxLength(254).IsRequired();
            e.Property(o => o.BillingAddressLine1).HasMaxLength(200).IsRequired();
            e.Property(o => o.BillingAddressLine2).HasMaxLength(200);
            e.Property(o => o.BillingCity).HasMaxLength(100).IsRequired();
            e.Property(o => o.BillingState).HasMaxLength(2).IsRequired();
            e.Property(o => o.BillingZip).HasMaxLength(10).IsRequired();
            e.Property(o => o.ContactId).HasMaxLength(100).IsRequired();
            e.Property(o => o.StoreName).HasMaxLength(250);
            e.Property(o => o.Provider).HasMaxLength(30);
            e.Property(o => o.ProviderSessionId).HasMaxLength(255);
            e.Property(o => o.ProviderPaymentId).HasMaxLength(255);
            e.Property(o => o.CardBrand).HasMaxLength(30);
            e.Property(o => o.CardLast4).HasMaxLength(4);
            e.Property(o => o.FailureReason).HasMaxLength(500);
            e.Property(o => o.RowVersion).IsRowVersion();
            e.HasOne(o => o.User).WithMany().HasForeignKey(o => o.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<FinalizedRecord>(e =>
        {
            e.HasIndex(r => r.PublicId).IsUnique();
            e.HasIndex(r => r.ConfirmationNumber).IsUnique();
            e.HasIndex(r => r.OrderId).IsUnique();
            e.HasIndex(r => new { r.UserId, r.ConfirmedAtUtc });
            e.HasIndex(r => r.ContactId);
            e.Property(r => r.ConfirmationNumber).HasMaxLength(32).IsRequired();
            e.Property(r => r.UserId).HasMaxLength(450).IsRequired();
            e.Property(r => r.CompanyName).HasMaxLength(200);
            e.Property(r => r.ContactId).HasMaxLength(100).IsRequired();
            e.Property(r => r.StoreName).HasMaxLength(250);
            e.Property(r => r.Response).HasMaxLength(1);
            e.Property(r => r.ConfirmedByUserId).HasMaxLength(450).IsRequired();
            e.Property(r => r.ConfirmedByName).HasMaxLength(256).IsRequired();
            e.HasOne(r => r.Order).WithOne(o => o.FinalizedRecord).HasForeignKey<FinalizedRecord>(r => r.OrderId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<AuditLog>(e =>
        {
            e.HasIndex(a => a.TimestampUtc);
            e.HasIndex(a => new { a.Category, a.TimestampUtc });
            e.Property(a => a.Category).HasMaxLength(30).IsRequired();
            e.Property(a => a.Action).HasMaxLength(100).IsRequired();
            e.Property(a => a.UserId).HasMaxLength(450);
            e.Property(a => a.UserName).HasMaxLength(256);
            e.Property(a => a.EntityType).HasMaxLength(100);
            e.Property(a => a.EntityId).HasMaxLength(100);
            e.Property(a => a.Details).HasMaxLength(2000);
            e.Property(a => a.IpAddress).HasMaxLength(64);
        });

        builder.Entity<CallLog>(e =>
        {
            e.HasIndex(c => c.PublicId).IsUnique();
            e.HasIndex(c => new { c.UserId, c.ContactId, c.StartedAtUtc });
            e.HasIndex(c => c.StartedAtUtc);
            e.Property(c => c.UserId).HasMaxLength(450).IsRequired();
            e.Property(c => c.ContactId).HasMaxLength(100).IsRequired();
            e.Property(c => c.StoreName).HasMaxLength(250);
            e.Property(c => c.SourceFile).HasMaxLength(100);
            e.Property(c => c.Outcome).HasConversion<string>().HasMaxLength(20);
            e.Property(c => c.Notes).HasMaxLength(CallLog.MaxNotesLength);
            e.HasOne(c => c.User).WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<AppSetting>(e =>
        {
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasMaxLength(100);
            e.Property(s => s.Value).HasMaxLength(2000).IsRequired();
            e.Property(s => s.UpdatedBy).HasMaxLength(256);
        });
    }
}
