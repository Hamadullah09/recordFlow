# RecordFlow — Store Contact Verification Portal

A secure, responsive ASP.NET Core (.NET 10) web portal for U.S. businesses. Users upload a CSV, pick a store
record, check out with a PCI-compliant payment provider, share a secure form so the store can fill in missing
contact/emergency/alarm information, verify the result, and get a confirmation and receipt.

**Uploaded CSV files are never stored permanently.** They are parsed in memory into an encrypted, per-user,
auto-expiring workspace. Only the final, confirmed business record is saved to SQL Server.

```
Upload CSV → Click a record (details popup, call timer starts; "End call" sets Close Time)
→ Proceed → Form (store + billing details) → Share form → Recipient adds missing info and fills/edits
billing details → Record updates automatically → User verifies & confirms → Payment gateway → Receipt
```

Times are shown in Pakistan Standard Time (`App:TimeZone` = `Asia/Karachi`).

## Technology

| Layer | Technology |
|---|---|
| Web / backend | ASP.NET Core 10 Razor Pages + REST API controllers, C# |
| Data | Entity Framework Core 10 + SQL Server (migrations included) |
| Identity | ASP.NET Core Identity (PBKDF2 password hashing, lockout, email confirmation, password reset, roles) |
| Temporary workspace | `IDistributedCache` (in-memory for one server, Redis for many), encrypted with ASP.NET Core Data Protection |
| Payments | Stripe Checkout (hosted, PCI DSS Level 1; Visa, Mastercard, American Express, Discover, wallets, optional ACH) |
| CSV parsing | CsvHelper |
| Email | MailKit (SMTP); in-app development mailbox locally |
| UI | Bootstrap 5.3, custom responsive SaaS theme, no inline scripts (strict CSP) |
| Tests | xUnit (56 unit + workflow integration tests) |

## Solution layout

```
RecordFlow.sln
├─ src/RecordFlow.Core            Domain entities, workspace model, interfaces, pure business logic
│                                 (form builder, dynamic column visibility, validation, pricing, tokens)
├─ src/RecordFlow.Infrastructure  EF Core DbContext + migrations, encrypted workspace store, CSV importer,
│                                 Stripe & simulated payment providers, email, audit logging, workflow service
├─ src/RecordFlow.Web             Razor Pages (user portal, account, recipient form), Admin area,
│                                 JSON API, security middleware, wwwroot (CSS/JS)
├─ tests/RecordFlow.Tests         xUnit tests
├─ deploy/                        SQL schema script, backup script, production config example
├─ docs/                          ARCHITECTURE.md, SECURITY.md, DEPLOYMENT.md
└─ samples/stores-sample.csv      Example CSV (includes missing fields and an extra column)
```

## Run locally (Windows, macOS, Linux)

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download) and SQL Server (LocalDB, Express, Developer or a container).

```bash
dotnet dev-certs https --trust
```

```bash
dotnet run --project src/RecordFlow.Web --launch-profile https
```

Open https://localhost:7290. In Development the app:

- applies EF Core migrations and seeds roles, the six admin columns, default form fields and an administrator;
- uses the **simulated payment gateway** (no keys needed, never asks for card data);
- captures all emails at **https://localhost:7290/dev/mailbox** (use it to confirm accounts and open share links).

The connection string in `src/RecordFlow.Web/appsettings.Development.json` targets `(localdb)\MSSQLLocalDB`.
Change it for SQL Express/Docker, e.g. `Server=localhost;Database=RecordFlow_Dev;User Id=sa;Password=...;TrustServerCertificate=True`.

**Development administrator:** the seed credentials are in `appsettings.Development.json` under `Seed`
(development only — production requires you to supply your own, see the deployment guide).

### Try the workflow

1. Register an account, then confirm it from the dev mailbox.
2. On the dashboard click **Upload CSV** and choose `samples/stores-sample.csv`.
3. Click a record row to see all its CSV details in a popup (the call timer starts; **End call** records the close time).
4. Click **Proceed** → the form opens → **Share form** (copy the link or send it by email — it appears in the dev mailbox).
5. Open the link in a private window as the recipient, fill in the missing fields and the billing details, and submit.
6. Back in the portal the record is **Ready for verification** → **Verify details** → **Confirm & pay** → *Simulate successful payment* → receipt.
7. Sign in as the administrator and open **Admin Portal** to manage users, the six dashboard columns, form fields,
   pricing, finalized records (CSV export), payments and the audit log.

## Tests

```bash
dotnet test
```

The suite covers CSV validation and parsing (missing fields, extra columns, binary/oversized files, BOM/delimiters),
form generation, dynamic admin-column visibility, encrypted workspace isolation between users, CSV-injection
escaping, field validation, pricing, and a full workflow integration test (payment → share → recipient → confirm),
including single-use share links, tamper attempts on non-editable/payment fields, and cross-user access attempts.

## Documentation

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — components, temporary vs. permanent data, workflow state machine, database schema
- [docs/SECURITY.md](docs/SECURITY.md) — how each security requirement is implemented
- [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) — configuration reference, Stripe/SMTP/Redis setup, Azure & IIS deployment, backups, operations
