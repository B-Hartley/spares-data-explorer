# Deploy Spares Data Explorer to IIS

This application uses the same .NET 10 / Razor Pages / IIS hosting model as the Whitemeadow dashboard. It is a separate application with a separate cookie, account database, key directory and import folder.

## 1. Build or download

The GitHub Actions **Build, verify and package** workflow produces `spares-data-explorer-IIS`. Download and extract it, or build on a machine with the .NET 10 SDK:

```powershell
.\Scripts\Build-Windows.cmd
```

The self-contained Windows output is in `Publish-IIS`. The server still requires the ASP.NET Core Hosting Bundle for the IIS module.

## 2. Create the application

1. Install the .NET 10 Hosting Bundle on the Windows server and restart IIS if the installer requires it.
2. Create a separate application pool: **No Managed Code**, integrated pipeline, 64-bit enabled. Use one worker process.
3. Create a site/application pointing to the extracted publish directory, with the intended hostname and an HTTPS certificate.
4. Grant the application-pool identity read/execute permission on the application directory and **Modify only on `Data`**. The app needs to maintain accounts, data-protection keys, imports and usage logs there.
5. Set `ASPNETCORE_ENVIRONMENT=Production`. Do not deploy the Development HTTP setting.
6. If using `appsettings.json`, create it locally from `ReferenceConfig/appsettings.example.json` and set AllowedHosts to the intended hostname.

The release excludes `Data` and server-local `appsettings*.json`. Do not copy a development account database or keys into production.

## 3. Create the first administrator

Open a console in the application directory under an account with permission to write `Data`:

```powershell
.\ClickSpares.SparesExplorer.exe --create-admin your.name@clickspares.co.uk
```

Enter the password at the prompt. Passwords are not supplied as command-line arguments, printed, or stored in plaintext. The initial version accepts Click Spares staff accounts only.

Create `Data\Auth\admin_allowed_ips.txt` with the exact IP address(es) from which administration is allowed, one per line. Missing or unreadable lists deny administration. Comments begin with `#`.

```text
# Replace with your real, approved administrator IP address.
# Loopback entries are suitable only for administering on the server itself.
127.0.0.1
::1
```

The application checks the immediate connection IP and does not accept arbitrary X-Forwarded-For headers. If an additional reverse proxy fronts IIS, configure the trusted proxy boundary explicitly before relying on an administrator IP allowlist. Do not allowlist a public proxy in a way that gives every visitor administrative network access.

## 4. Load the CSV exports

Sign in from an allowed administrator address. Choose **Data imports**, select all seven CSV files, validate them, review the diagnostic messages and click **Use this dataset**.

The source files are stored in `Data\Imports`. Users query authenticated server-side aggregates; the raw files are not downloadable from the web root. No real customer or business exports are included in the repository or release.

For an initial functional demonstration, copy the seven `SampleData` files from the source checkout into `Data\Imports`. They contain synthetic data only. Remove or replace them before analysing real demand.

## 5. Updating the application

Stop the application pool or use `app_offline.htm`, replace the published application files, then restart the pool. **Preserve the complete existing `Data` directory and local `appsettings.json`.** Do not use a mirroring command that deletes folders absent from the new release.

Back up `Data` separately, with restricted access: it includes the import snapshot, previous snapshot, accounts, data-protection keys and usage history. Keys must survive restarts so session cookies remain valid. Deploying multiple independent workers against the same account/log files is not supported by this prototype.

## Local development

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --urls http://localhost:5080
```

Development HTTP is for a loopback connection only. It does not provide a public hosting endpoint. The repository and IIS build artifact also do not automatically create an IIS hostname or configure a server.
