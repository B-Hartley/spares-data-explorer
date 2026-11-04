# Portable interactive prototype

Open `spares-prototype.html` in a modern browser. It is one self-contained file, with the dashboard, charts, sample data and query engine included. It works without a server or internet connection.

All the data is invented and labelled **DEMO**. This is a demonstration of filtering, calculations and drill-down; it is not a report of Click Spares activity. The permanent green notice and the sidebar identify it as a synthetic demonstration.

Try the following:

1. Compare the factory rows, then select a factory to see its products.
2. Select a product to see its BaseParts, or choose the BaseParts tab to compare parts across the current factory selection.
3. Select a BasePart to inspect the matching requests and requested part lines.
4. Switch between requested units, distinct requests and part lines.
5. Filter several factories together, change the date range, or focus on demand that was not fulfilled.
6. Export all the filtered group results to CSV.

The demo has no account-management, login or import functionality. These remain in the separate ASP.NET Core application and require staff authentication and the configured administrator controls. The demo query engine handles its own read operations in the browser; other API routes are rejected, and its content-security policy blocks network data requests.

## Rebuild

From the repository root:

```bash
node Scripts/Generate-Sample-Data.mjs
node Scripts/Build-Demo.mjs
```

The builder reads only the fixed `SampleData` directory and the dashboard source. It refuses unexpected columns, non-DEMO identifiers, non-demo manufacturer/factory labels and symlinked sample inputs. It does not accept a path to business exports or read `Data/Imports`.

`node Scripts/Build-Demo.mjs --preview` also writes an identical copy to a sibling `demo-preview/dist/index.html` and a static preview manifest in that sibling directory. It does not start a server or change the production application's hosting configuration.

After changes to the dashboard, stylesheet or demo engine, rebuild this file so the portable prototype stays current.
