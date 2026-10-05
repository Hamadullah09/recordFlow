# Security implementation

How each security requirement is met. File references are relative to the repository root.

| Requirement | Implementation |
|---|---|
| **ASP.NET Core Identity** | `AddIdentity<ApplicationUser, IdentityRole>` with EF Core stores (`src/RecordFlow.Web/Program.cs`). |
| **Secure password hashing** | Identity's PBKDF2 (HMAC-SHA512, 100k iterations, per-user salt). Admins never see or set passwords — new users get an expiring set-password link. |
| **Password strength** | Min. 10 chars, upper, lower, digit, symbol, 4 unique chars; client-side strength meter. |
| **Account verification** | `RequireConfirmedEmail` (on by default); single-use, 3-hour Data Protection tokens. |
| **Password reset** | Forgot-password flow with expiring tokens; identical response whether or not the account exists. |
| **Lockout / rate limiting** | 5 failed sign-ins → 15-minute lockout. ASP.NET Core rate limiter: auth pages 10/min/IP, uploads 10/5 min/user, sharing 20/10 min/user, recipient form 30/min/IP, API 120/min/user. |
| **Duplicate User ID / email** | Pre-checks plus Identity unique indexes on `NormalizedUserName` and `NormalizedEmail`. |
| **Session management / expiration** | HttpOnly, Secure, SameSite=Lax auth cookie; 60-minute sliding expiry (configurable). Security stamp re-validated every 5 minutes, so disabled users and password changes take effect on existing sessions. Signing out deletes the temporary workspace. |
| **Role-based authorization** | Roles `User` and `Administrator`. Conventions require `PortalUser` for all pages by default; anonymous access is opt-in per page. The whole `Admin` area requires `AdminOnly`. Admins cannot disable themselves or remove their own admin role. |
| **Separate Admin Portal** | Own area (`/Admin`), layout and policy; optional IP allow-list (`AdminPortal:AllowedIps`) returns 404 to other networks; `noindex`. |
| **Authorization on every protected resource** | Records are only resolved inside the caller's workspace; orders/receipts are filtered by `UserId`; recipient access requires a valid token hash; admin pages are covered by the area policy. |
| **Unauthorized record access** | Unguessable keys + owner scoping (see `docs/ARCHITECTURE.md` › Isolation). Covered by `WorkflowIntegrationTests.Other_users_cannot_reach_a_record_even_with_its_key`. |
| **Secure sharing tokens** | 256-bit random tokens (`SecureTokens.NewToken`); only SHA-256 hashes index the cache; constant-time comparison; single use; revocable; expire with the workspace; no database ids in URLs; `Referrer-Policy: no-referrer` and `X-Robots-Tag: noindex` on `/f/*`. |
| **Recipient cannot change payment data** | Payment/billing data is not part of the form model; it is rendered read-only from the `Orders` table. The server applies only fields an admin marked recipient-editable; any other posted keys are ignored (tested). |
| **HTTPS** | `UseHttpsRedirection` + HSTS (1 year, include subdomains) outside Development; `upgrade-insecure-requests` in CSP; Secure cookies. |
| **Anti-CSRF** | Antiforgery tokens on all Razor Pages forms (default), `[ValidateAntiForgeryToken]` on API POSTs with the token in the `RequestVerificationToken` header. The Stripe webhook is exempt and authenticated by its signature. |
| **Input validation** | DataAnnotations on all input models (client + server); dynamic fields validated server-side by `FieldValidator` (U.S. phone, ZIP/ZIP+4, state, email, length, options). |
| **Output encoding / XSS** | Razor HTML-encodes all output; no `Html.Raw` of user data; strict CSP (`script-src 'self'`, `style-src 'self'`, no inline scripts/styles, `object-src 'none'`, `frame-ancestors 'none'`); email templates HTML-encode values; toasts use `textContent`. |
| **SQL injection** | EF Core parameterized queries only; no raw SQL. |
| **Secure file upload** | `.csv` extension and content-type allow-list; size limit enforced by `FormOptions` and while reading; magic-byte check rejects ZIP/XLSX, PDF, EXE, OLE and NUL-containing files; strict UTF-8 decode with fallback; row/column/value-length limits; files are processed in memory and never written to disk. |
| **CSV injection** | Control characters stripped on import; all admin CSV exports prefix cells starting with `= + - @ TAB CR` (and full-width variants) with `'`. |
| **Temporary data protection** | Workspaces encrypted with Data Protection (AES-256-CBC + HMAC-SHA256) before entering the cache; keys persisted in SQL Server and optionally encrypted with a certificate. |
| **Secure payment integration** | Stripe Checkout (hosted page; SAQ A scope). Amounts are computed server-side and verified against the provider after payment; webhook signatures verified; idempotency keys; only brand + last 4 stored. |
| **Security headers** | `X-Content-Type-Options`, `X-Frame-Options: DENY`, `Referrer-Policy`, `Permissions-Policy`, `Cross-Origin-Opener-Policy`, CSP, and `Cache-Control: no-store` on all dynamic pages. |
| **Host header attacks** | Absolute links (emails, payment return URLs, share links) use `App:PublicBaseUrl`, which is mandatory outside Development; set `AllowedHosts`. |
| **Audit logging** | `AuditLogs` table: sign-ins (success/failure/lockout), registration, password events, profile changes, all admin changes (users, roles, companies, columns, fields, pricing, exports), CSV uploads (file name and counts only), sharing, recipient submissions, payment transitions and confirmations, with user and IP. Audit writes use a separate DbContext and never break the request. |
| **Error handling** | Generic error page with request id in production (no stack traces), friendly status pages (404/403/429…), ProblemDetails for the API. |
| **Database backups** | See `deploy/sql/backup-database.sql` and `docs/DEPLOYMENT.md` › Backups. CSV data is never in the database, so never in backups. |

## Operational recommendations

- Keep secrets (connection strings, Stripe keys, SMTP password, certificate password) in Azure Key Vault or environment variables.
- Use a managed identity for Azure SQL; grant the app only `db_datareader`, `db_datawriter` (+ `db_ddladmin` only for the migration job).
- Turn on Azure SQL auditing / Microsoft Defender for SQL, and forward application logs to Application Insights or your SIEM.
- Review the audit log for `LoginFailed`, `LoginLockedOut` and `Payment*` failures; set alerts on spikes.
- Enable MFA for administrator accounts at the identity-provider level if you federate sign-in, or add Identity's
  authenticator-app 2FA (the token providers are already registered via `AddDefaultTokenProviders`).
- Run a dependency scan (`dotnet list package --vulnerable`) in CI and keep the .NET runtime patched.
