# Deployment & configuration

## 1. Configuration reference

All settings can be supplied through `appsettings.Production.json`, environment variables (`Section__Key`, e.g.
`Payments__Stripe__SecretKey`) or Azure App Configuration / Key Vault. An annotated example is in
`deploy/appsettings.Production.example.json`.

| Key | Required in production | Description |
|---|---|---|
| `ConnectionStrings:DefaultConnection` | ✔ | SQL Server / Azure SQL connection string. |
| `App:PublicBaseUrl` | ✔ | Public `https://` address. Used for every absolute link (emails, payment returns, share links). The app refuses to start without it outside Development. |
| `App:TimeZone` | | Display time zone (IANA or Windows id). Default `America/New_York`. |
| `App:Name`, `App:SupportEmail` | | Branding shown in the UI and emails. |
| `AllowedHosts` | ✔ | Your host name(s), e.g. `portal.example.com`. |
| `Identity:RequireConfirmedEmail` | | Default `true`. |
| `Identity:SessionTimeoutMinutes` | | Sliding sign-in session length. Default 60. |
| `Workspace:Provider` | ✔ for >1 server | `Memory` (single instance) or `Redis`. |
| `Workspace:RedisConnectionString` | with Redis | e.g. Azure Cache for Redis (TLS, port 6380). |
| `Workspace:IdleTimeoutMinutes` | | Temporary workspace deleted after this much inactivity. Default 120. Choose a value long enough for recipients to respond. |
| `Workspace:MaxLifetimeHours` | | Absolute workspace / share-link lifetime. Default 24. |
| `CsvImport:MaxFileBytes`, `MaxRows`, `MaxColumns`, `MaxValueLength` | | Upload limits (defaults 5 MB, 5,000 rows, 100 columns, 2,000 chars). |
| `Payments:Provider` | ✔ | `Stripe`. (`Simulated` is rejected outside Development.) |
| `Payments:Stripe:SecretKey` | ✔ | `sk_live_…` (restricted key with Checkout Sessions write + PaymentIntents read is sufficient). |
| `Payments:Stripe:WebhookSecret` | ✔ | `whsec_…` from the webhook endpoint. |
| `Payments:EnableAch` | | Offer U.S. bank account (ACH) payments. Enable *ACH Direct Debit* in Stripe first. |
| `Email:Provider` | ✔ | `Smtp` (required outside Development). |
| `Email:Smtp:Host/Port/UseStartTls/UserName/Password`, `Email:FromAddress`, `Email:FromName` | ✔ | Any SMTP relay (SendGrid, Amazon SES, Microsoft 365, Mailgun…). Configure SPF, DKIM and DMARC for the From domain. |
| `DataProtection:CertificatePath` / `CertificatePassword` | recommended | PFX used to encrypt the Data Protection key ring at rest. |
| `Database:MigrateOnStartup` | | Default `false` in production; apply migrations as a release step instead. |
| `AdminPortal:AllowedIps` | | Optional allow-list for `/Admin`. |
| `ReverseProxy:KnownProxies` | behind a proxy | IPs of proxies/load balancers whose `X-Forwarded-*` headers are trusted. |
| `Seed:AdminUserId` / `AdminEmail` / `AdminPassword` / `AdminFullName` | first deploy | Creates the first administrator if that User ID doesn't exist. Remove the password afterwards. |

## 2. Database

1. Create an empty database (Azure SQL: S1/GP_S_Gen5_1 or higher is plenty to start).
2. Apply the schema — either:
   - run `deploy/sql/RecordFlow-schema.sql` (idempotent; safe to re-run), or
   - from a build agent: `dotnet ef database update --project src/RecordFlow.Infrastructure --startup-project src/RecordFlow.Web`
3. Start the app once with the `Seed:*` settings to create roles, the six admin columns, default form fields and the
   first administrator (`DbSeeder` also runs when `Database:MigrateOnStartup=true`). Alternatively run it once from a
   release job with `Database__MigrateOnStartup=true`.

Regenerate the script after model changes:

```bash
dotnet ef migrations add <Name> --project src/RecordFlow.Infrastructure --startup-project src/RecordFlow.Web --output-dir Data/Migrations
```

```bash
dotnet ef migrations script --idempotent --project src/RecordFlow.Infrastructure --startup-project src/RecordFlow.Web --output deploy/sql/RecordFlow-schema.sql
```

## 3. Stripe

1. Create a Stripe account, complete activation, and enable the card brands you need (Visa, Mastercard, American
   Express and Discover are on by default for U.S. accounts). Optionally enable Apple Pay / Google Pay and ACH Direct Debit.
2. Copy the secret key into `Payments:Stripe:SecretKey`.
3. Add a webhook endpoint `https://<your-domain>/api/payments/stripe/webhook` with events:
   `checkout.session.completed`, `checkout.session.async_payment_succeeded`, `checkout.session.async_payment_failed`,
   `checkout.session.expired`. Put its signing secret in `Payments:Stripe:WebhookSecret`.
   The endpoint's API version must match the Stripe.net library version (53.x) — select the matching version when
   creating the endpoint, or the signature check will reject events.
4. Test with Stripe test keys and card `4242 4242 4242 4242` before switching to live keys. Locally, forward webhooks with
   `stripe listen --forward-to https://localhost:7290/api/payments/stripe/webhook`.
5. Sales tax: the Admin Portal applies one flat rate. For per-state U.S. sales tax, enable Stripe Tax and set
   `AutomaticTax = new() { Enabled = true }` in `StripePaymentProvider` (and set the tax rate in the portal to 0).

PCI scope: card data is entered only on Stripe's hosted Checkout page, so the application qualifies for SAQ A.
Never add card fields to the application's own pages.

## 4. Hosting options

### Azure App Service (recommended)

1. Create: App Service plan (Linux or Windows, P0v3+), Web App (.NET 10), Azure SQL Database, Azure Cache for Redis
   (if you will run more than one instance), Key Vault, Application Insights.
2. Enable the web app's **managed identity**; grant it Key Vault *Secrets User* and an Azure SQL user
   (`CREATE USER [<app-name>] FROM EXTERNAL PROVIDER; ALTER ROLE db_datareader ADD MEMBER …; ALTER ROLE db_datawriter ADD MEMBER …;`).
3. App settings: `ASPNETCORE_ENVIRONMENT=Production`, the keys from section 1 (use Key Vault references
   `@Microsoft.KeyVault(SecretUri=…)` for secrets).
4. Settings → Configuration: **HTTPS Only = On**, minimum TLS 1.2, FTP disabled. Add your custom domain + managed certificate.
5. With more than one instance, set `Workspace:Provider=Redis` and keep **ARR affinity on** (workspace updates are
   serialized per process).
6. Health check path: `/health`.
7. Deploy:

```bash
dotnet publish src/RecordFlow.Web -c Release -o ./publish
```

   then zip-deploy `./publish` (or use the GitHub Actions / Azure DevOps App Service task).

### IIS (Windows Server)

1. Install the **ASP.NET Core 10 Hosting Bundle** and restart IIS.
2. Publish (`dotnet publish -c Release`), copy to the site folder, create an app pool with *No Managed Code*.
3. Bind HTTPS with a trusted certificate; add an HTTP→HTTPS redirect.
4. Set environment variables in the app pool or `web.config` (`<environmentVariables>`), or use `appsettings.Production.json`
   with NTFS permissions restricted to the app pool identity.
5. Give the app pool identity access to SQL Server (Windows auth) or use a SQL login stored as a secret.

### Containers

A `Dockerfile` is provided (listens on port 8080, runs as non-root). Terminate TLS at the ingress/load balancer, set
`ReverseProxy:KnownProxies` (or configure `ForwardedHeadersOptions.KnownNetworks`) so the app sees the original scheme,
and point liveness/readiness probes at `/health`.

### Continuous deployment (GitHub Actions → myASP.NET)

`.github/workflows/ci-cd.yml` builds and tests every push and pull request. A push to `main` that passes is
published and deployed to myASP.NET with Web Deploy, then `https://recordflow.sma-techno.net/health` is checked.
You can also run it by hand from the repository's **Actions** tab (*CI/CD → Run workflow*).

One-time setup:

1. myASP.NET control panel → Websites → recordflow → ⋯ → **VS Webdeploy** → *Turn on*.
2. GitHub → repository → Settings → Secrets and variables → Actions:
   - Variables: `WEBDEPLOY_SERVER` = `win8238.site4now.net`, `WEBDEPLOY_SITE` = `smatechnologies-001-site15`
   - Secrets: `WEBDEPLOY_USERNAME`, `WEBDEPLOY_PASSWORD` (from the VS Webdeploy dialog)

What a deployment does and doesn't touch:

- Uploads the new build with `app_offline.htm` in place, so IIS releases the DLLs while files are copied.
- **Never** overwrites `appsettings.Production.json` (connection string, keys, seed) or the `logs` folder, and never
  deletes files that aren't part of the build. Keep all server-specific settings in `appsettings.Production.json`;
  `appsettings.json` is replaced by the repository copy on every deploy.
- Database migrations run when the app starts (`Database:MigrateOnStartup = true` on the server).
- `appsettings.Development.json` is never published.

If a deployment's health check fails, open `logs\stdout_*.log` on the server (enable `stdoutLogEnabled` in
`web.config` temporarily if no log is written) and roll back by re-running the last good workflow run.

## 5. Email

Use a transactional email provider and authenticate the From domain (SPF, DKIM, DMARC) so confirmation, reset and
share emails reach the inbox. The app sends: account confirmation, password reset / set-password, share-form
invitations and "recipient submitted" notifications.

## 6. Backups & recovery

- **Azure SQL:** automated backups with point-in-time restore (7–35 days). Add long-term retention (e.g. weekly for
  12 months) and geo-redundant backup storage. Test a restore quarterly.
- **SQL Server on a VM / on-premises:** schedule `deploy/sql/backup-database.sql` with SQL Server Agent (full nightly,
  differential every 6 h, log every 15 min), encrypt backups, copy them off-site, test restores monthly.
- Back up the Data Protection certificate (PFX) and its password in Key Vault — without the key ring, existing
  sessions, tokens and in-flight workspaces cannot be decrypted (permanent data is unaffected).
- Temporary workspaces are intentionally not backed up.

## 7. Operations

- **Logging:** standard `ILogger` → console/Event Log; add Application Insights (`Microsoft.ApplicationInsights.AspNetCore`)
  or Serilog sinks as needed. Logs never include CSV contents, passwords or card data.
- **Audit log:** Admin Portal › Audit log (filter by category, user, date, failures).
- **Health:** `GET /health` returns 200 when the database is reachable.
- **Stuck payments:** Admin Portal › Payments › order › *Refresh status from provider* re-checks a pending payment
  (useful if a webhook was missed).
- **Configuration changes** to columns/fields are cached for up to 10 minutes on other instances.

## 8. Go-live checklist

- [ ] `ASPNETCORE_ENVIRONMENT=Production`, `App:PublicBaseUrl` and `AllowedHosts` set
- [ ] Live Stripe keys + webhook secret in Key Vault; webhook endpoint receiving events
- [ ] SMTP configured; SPF/DKIM/DMARC passing
- [ ] Redis configured if scaled out; ARR affinity on
- [ ] Data Protection certificate configured
- [ ] Schema applied; first administrator created; seed password removed from configuration
- [ ] Pricing reviewed in Admin Portal › Pricing & settings
- [ ] Privacy notice and Terms pages replaced with reviewed versions
- [ ] Backups / long-term retention configured and a restore tested
- [ ] Optional: `AdminPortal:AllowedIps`, WAF (Azure Front Door / Application Gateway), alerts on audit failures
