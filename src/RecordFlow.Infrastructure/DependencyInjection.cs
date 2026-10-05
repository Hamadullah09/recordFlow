using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RecordFlow.Core.Abstractions;
using RecordFlow.Infrastructure.Auditing;
using RecordFlow.Infrastructure.Csv;
using RecordFlow.Infrastructure.Data;
using RecordFlow.Infrastructure.Email;
using RecordFlow.Infrastructure.Payments;
using RecordFlow.Infrastructure.Services;
using RecordFlow.Infrastructure.Settings;
using RecordFlow.Infrastructure.Workspaces;

namespace RecordFlow.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration, IHostEnvironment env)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");

        services.AddDbContext<ApplicationDbContext>(o =>
            o.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(5)));

        services.Configure<AppOptions>(configuration.GetSection(AppOptions.SectionName));
        services.Configure<WorkspaceOptions>(configuration.GetSection(WorkspaceOptions.SectionName));
        services.Configure<CsvImportOptions>(configuration.GetSection(CsvImportOptions.SectionName));
        services.Configure<PaymentOptions>(configuration.GetSection(PaymentOptions.SectionName));
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));

        services.AddSingleton(TimeProvider.System);
        services.AddMemoryCache();
        services.AddHttpContextAccessor();

        // Data Protection keys are kept in SQL Server so encrypted workspaces, auth cookies and
        // antiforgery tokens stay valid across restarts and web farm instances.
        var dataProtection = services.AddDataProtection()
            .SetApplicationName("RecordFlow")
            .PersistKeysToDbContext<ApplicationDbContext>();

        // Production: encrypt the key ring at rest with a certificate (PFX), e.g. one deployed from Key Vault.
        var certPath = configuration["DataProtection:CertificatePath"];
        if (!string.IsNullOrWhiteSpace(certPath))
        {
            dataProtection.ProtectKeysWithCertificate(
                X509CertificateLoader.LoadPkcs12FromFile(certPath, configuration["DataProtection:CertificatePassword"]));
        }

        // Temporary workspace storage – deliberately NOT the permanent database.
        var workspace = configuration.GetSection(WorkspaceOptions.SectionName).Get<WorkspaceOptions>() ?? new WorkspaceOptions();
        if (string.Equals(workspace.Provider, "Redis", StringComparison.OrdinalIgnoreCase))
        {
            services.AddStackExchangeRedisCache(o =>
            {
                o.Configuration = workspace.RedisConnectionString;
                o.InstanceName = "recordflow:";
            });
        }
        else
        {
            services.AddDistributedMemoryCache();
        }
        services.AddSingleton<IWorkspaceStore, DistributedWorkspaceStore>();

        services.AddSingleton<ICsvImportService, CsvImportService>();
        services.AddScoped<IAuditLogger, AuditLogger>();
        services.AddScoped<IAppSettingsService, AppSettingsService>();
        services.AddScoped<PortalConfigService>();
        services.AddScoped<OrderService>();
        services.AddScoped<RecordWorkflowService>();

        // Payments
        var payments = configuration.GetSection(PaymentOptions.SectionName).Get<PaymentOptions>() ?? new PaymentOptions();
        if (string.Equals(payments.Provider, "Simulated", StringComparison.OrdinalIgnoreCase))
        {
            if (!env.IsDevelopment())
                throw new InvalidOperationException("The Simulated payment provider can only be used in the Development environment.");
            services.AddSingleton<SimulatedPaymentProvider>();
            services.AddSingleton<IPaymentProvider>(sp => sp.GetRequiredService<SimulatedPaymentProvider>());
        }
        else
        {
            services.AddSingleton<StripePaymentProvider>();
            services.AddSingleton<IPaymentProvider>(sp => sp.GetRequiredService<StripePaymentProvider>());
        }

        // Email
        var emailOptions = configuration.GetSection(EmailOptions.SectionName).Get<EmailOptions>() ?? new EmailOptions();
        if (string.Equals(emailOptions.Provider, "Smtp", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IAppEmailSender, SmtpEmailSender>();
        }
        else
        {
            if (!env.IsDevelopment())
                throw new InvalidOperationException("Email:Provider must be 'Smtp' outside the Development environment.");
            services.AddSingleton<DevMailbox>();
            services.AddSingleton<IAppEmailSender, DevelopmentEmailSender>();
        }

        return services;
    }
}
