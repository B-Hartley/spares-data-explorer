# Security boundaries

This prototype is intended for approved Click Spares staff, with one application process on a protected IIS deployment.

## Access

Every business-data route requires a current, enabled `clickspares.co.uk` account. External manufacturer/factory data permissions have not been implemented. Do not widen the allowed email domains to grant a client access: a client-facing version requires server-enforced data scopes on summaries, filter options, drill-downs, exports and diagnostics.

Administration additionally requires an administrator role and an exact match against the immediate connection IP in `Data/Auth/admin_allowed_ips.txt`. A missing list denies administration. The server does not trust caller-supplied forwarding headers.

Login uses individual accounts, rate limiting and password hashes. Password resets and account disablement revoke existing cookies through per-request security stamps. No default credentials, anonymous dashboard mode or public bootstrap route exists.

## Private state

`Data/Auth`, `Data/Imports`, `Data/Previous`, `Data/Staging` and `Data/Logs` are outside the web root and excluded from Git and publication. Protect these directories and backups using operating-system permissions. Retain data-protection keys between deployments; do not copy them between applications.

The browser receives only authorized query results. The prototype does not persist query results or imported CSV files in browser local storage. The public source repository must contain synthetic sample data only.

## Imports

CSV uploads require administrator authorization and anti-forgery validation. Names are mapped to a fixed list; supplied paths are never used as storage paths. File count, individual size and total size are bounded. A complete candidate is parsed before the live snapshot changes. Validation tokens are random, expire after 20 minutes and belong to the uploading administrator.

Text rendered in the dashboard is escaped or assigned using `textContent`. Exports must neutralize spreadsheet formula prefixes before writing source labels as CSV cells.

## Operational limits

- HTTPS is required in Production. HTTP exceptions are for Development loopback traffic only.
- Use one IIS worker process; file-backed account, log and import mutation locks are process-local.
- Account lifecycle and CSV updates are audited through account/session records and server logs; this is not a tamper-proof compliance archive.
- Online minutes are estimated from session heartbeats. They are not a measure of active employee work.
- No SSO, password recovery email, automatic email verification or multi-factor authentication is implemented in the initial prototype.
- There is no production host configured by the source repository alone; follow DEPLOYMENT.md and preserve private runtime state during upgrades.

Report security concerns privately to the repository owner rather than posting business exports or account data in a public issue.
