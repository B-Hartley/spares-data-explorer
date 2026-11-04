# Click Spares — Spares Data Explorer

An authenticated ASP.NET Core 10 dashboard for exploring spare-parts demand and fulfilment. It follows the Razor Pages, plain JavaScript, server-local CSV and IIS approach used by the Whitemeadow swatch site.

## Try the portable prototype

Download [demo/spares-prototype.html](demo/spares-prototype.html) and open it in a modern browser to try the working filters, charts, drill-downs, request details and CSV export. The file runs offline and contains **synthetic DEMO data only**.

This portable demonstration is separate from the authenticated server application below. Real CSVs, user management and imports remain protected by staff sign-in and administrator controls. Rebuild the demo with `npm run demo`; see [the demo guide](demo/README.md) for details.

## What you can do

- Compare demand, fulfilled demand, unfulfilled demand and fulfilment percentage by manufacturer, factory, product or BasePart.
- Switch between requested part quantities, distinct customer requests and requested part lines.
- Drill from factory to product to BasePart, or compare BaseParts across several selected factories.
- Filter live by date range, manufacturer, factory, product, BasePart, original request status and customer code.
- Change between request date and despatch date; view daily, weekly or monthly demand trends.
- Review the matching request records behind a group.
- Validate seven CSV exports before applying a complete replacement dataset.
- Manage individual staff accounts and review login/session history from an allowed administrator IP address.

The initial version permits **approved `clickspares.co.uk` staff accounts only**. Manufacturer/factory data scopes for external clients are not yet implemented; external email domains cannot use the business-data APIs.

## Run locally

Install the .NET 10 SDK. From the repository directory:

```sh
dotnet run -- --create-admin your.name@clickspares.co.uk
```

The command prompts for a password. No default or shared password is shipped. Create `Data/Auth/admin_allowed_ips.txt` with one explicitly allowed administrator IP per line. For local development:

```text
127.0.0.1
::1
```

Then run:

```sh
dotnet run --urls http://localhost:5080 --environment Development
```

Open `http://localhost:5080`, sign in and use **Data imports** to select the seven CSV exports. HTTP development access is limited to the loopback connection. Production requires HTTPS. See [DEPLOYMENT.md](DEPLOYMENT.md) for Windows/IIS instructions.

The repository includes only clearly labelled synthetic examples in `SampleData/`. Real business data, users, password hashes, data-protection keys, staging files and usage logs are excluded from Git and publish output.

## CSV contract

File names are case-insensitive. CSV fields may be quoted; quoted commas and escaped quotes are supported. UK dates are read explicitly, including `dd/MM/yyyy` and `dd/MM/yyyy HH:mm:ss`. ISO dates are also supported. Keys are strings and retain leading zeroes.

| File | Required columns | Purpose |
| --- | --- | --- |
| `CustomerRequests.csv` | `Date`, `Key`, `Product`, `Satisfied`, `DespatchDate`, `customer` | One row per request. Optional `BaseProduct` overrides the catalogue BaseProduct for that request. |
| `custreqparts.csv` | `CR_Key`, `PartID`, `Qty` | Requested part lines; repeated rows are preserved. |
| `products.csv` | `Catalogue`, `Product`, `BaseProduct`, `Description`, `Manufacturer`, `Factory` | Product catalogue and grouping labels. |
| `parts.csv` | `Product`, `PartID`, `Qty_Rqd`, `BasePart` | Maps a product-specific part identifier to a BasePart. |
| `baseparts.csv` | `BasePart`, `Description` | BasePart descriptions. |
| `manufacturers.csv` | `ManufacturerCode`, `Manufacturer`, `ChargeTo` | Manufacturer lookup metadata. |
| `factories.csv` | `FactoryID`, `FactoryName`, `Location`, `Retailer`, `Current` | Factory lookup metadata. |

### Relationships

`CustomerRequests.Key = CustReqParts.CR_Key`

`CustomerRequests.Product = Products.Product`

`(CustomerRequests.BaseProduct ?? Products.BaseProduct, CustReqParts.PartID) = (Parts.Product, Parts.PartID)`

`Parts.BasePart = BaseParts.BasePart`

**PartID alone is not a join key.** The same PartID can occur under many base products. Manufacturer and factory grouping labels come from Products. Their master tables are optional metadata joins and never multiply or discard demand when a name is absent or duplicated.

### Metric definitions

Only a trimmed, case-insensitive `Satisfied` value, or the accepted spelling `Satified`, means fulfilled. **Every other value means not fulfilled**, including `NotSatisfied`, `NotReported`, `NotReports`, `Processing` and blanks. A despatch date does not change this classification.

- **Requests:** distinct CustomerRequests.Key. Requests with no part lines still count unless a BasePart filter is selected.
- **Part units:** sum of CustReqParts.Qty. Parts.Qty_Rqd is an assembly quantity and is never used as a demand multiplier.
- **Part lines:** the number of supplied CustReqParts rows. A zero-quantity line is retained and is not converted into one unit.
- **Fulfilment:** fulfilled count divided by demand for the selected metric. Units and lines inherit the request's status; these exports do not identify partial shipments within one request.
- **BasePart request totals:** a request can contain several BaseParts, so group request counts are not additive. The headline total remains a distinct count.
- **Multiple filter selections:** OR within a dimension, AND between dimensions. A BasePart filter sums only the matching part lines and quantities.
- **Dates:** inclusive start/end dates; request date by default. Despatch-date filtering excludes requests without that date. Weekly buckets start Monday.

Demand is an absolute volume, not a product defect rate; no sales or installed-base denominator is supplied.

### Import behaviour

Each browser upload must include all seven files. The server stages and validates them, then displays counts and diagnostics. Clicking **Use this dataset** applies the whole validated snapshot. Re-uploading the same files replaces the tables rather than appending rows. Without a stable line ID, repeated request/part pairs cannot safely be deduplicated.

For the known export layouts, extra unquoted commas can be rejoined in BasePart descriptions, product descriptions and manufacturer names. Repairs are reported, and ambiguous or invalid rows are rejected. Correctly quoted source exports are preferred.

Server-side refresh detects settled CSV changes approximately every 30 seconds. Browser imports are the preferred way to change all seven tables together. Invalid refreshes retain the last valid in-memory snapshot. A previous full snapshot is retained in the server-local `Data/Previous` directory.

## Security and administration

- Individual email/password accounts; PBKDF2-SHA256 hashes with per-password random salts.
- Revocable, HTTP-only session cookies with strict same-site behaviour; secure cookies in production.
- Per-request account/security-stamp validation; disabling or resetting an account invalidates its existing sessions.
- Staff authorization on every data API. Admin role plus the real connection IP allowlist on user management, usage reporting, imports and reloads.
- Anti-forgery protection for state-changing requests and account forms. Login attempts are rate-limited by connection IP.
- Data, accounts and keys stay outside `wwwroot`; no unauthenticated data export endpoint exists.
- Separate cookie name and data-protection application identity from the Whitemeadow app.
- Usage reporting includes last login, login count and estimated online minutes. Heartbeats estimate session activity and do not measure keyboard activity.

See [SECURITY.md](SECURITY.md) for deployment boundaries and operational notes.

## Verification

```sh
dotnet build tests/Spares.SelfTests.csproj -c Release
dotnet run --project tests/Spares.SelfTests.csproj -c Release --no-build
dotnet run --project tests/ClickSpares.SparesExplorer.SecurityTest/ClickSpares.SparesExplorer.SecurityTest.csproj -c Release
dotnet run --project tests/ClickSpares.SparesExplorer.IntegrationTest/ClickSpares.SparesExplorer.IntegrationTest.csproj -c Release
npm run check
```

Tests use synthetic data to cover the composite join, request-specific BaseProduct, status rules, duplicate lines, zero quantities, requests without lines, filtering, totals, date handling and CSV validation. GitHub Actions also publishes a self-contained Windows IIS artifact and checks that private runtime folders are absent.

The HTTP integration suite runs the real application in memory with TestServer, an isolated temporary data directory and synthetic CSVs. It checks login cookies, antiforgery, staff and administrator access, session revocation, protected runtime paths, and CSV preview/apply behaviour without opening a network listener.
