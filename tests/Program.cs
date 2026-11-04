using System.Globalization;
using System.Text;
using ClickSpares.SparesExplorer.Models;
using ClickSpares.SparesExplorer.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

// This project contains synthetic data only. No operational customer data or benchmarks
// belong in this public repository. --data optionally reconciles a locally supplied snapshot.
using var fixture = new Fixture();
var store = fixture.CreateStore();
store.ReplaceSnapshot(SparesStore.LoadDirectory(fixture.Imports));
int passed = 0;
var cases = new List<(string Name, Action Run)>
{
    ("Request totals remain distinct from part lines and units", () =>
    {
        var s = store.Explore(new()).Summary;
        Equal(8, s.Requests); Equal(3, s.FulfilledRequests);
        Equal(9, s.Lines); Equal(5, s.FulfilledLines);
        Equal(30m, s.Units); Equal(11m, s.FulfilledUnits);
        Equal(2, s.NoPartRequests); Equal(37.5m, s.RequestFulfilment);
        Equal(19m, s.UnfulfilledUnits);
    }),
    ("Satisfied status, including its supported typo, controls fulfilment", () =>
    {
        var fulfilled = store.Explore(new() { Outcome = "fulfilled" }).Summary;
        Equal(3, fulfilled.Requests); Equal(11m, fulfilled.Units);
        var unmet = store.Explore(new() { Outcome = "unfulfilled" }).Summary;
        Equal(5, unmet.Requests); Equal(19m, unmet.Units);
        Equal(0, unmet.FulfilledRequests);
        var demandOnly = store.Explore(new() { Statuses = ["NotReported", "Processing"] }).Summary;
        Equal(2, demandOnly.Requests); Equal(9m, demandOnly.Units); Equal(0, demandOnly.FulfilledRequests);
    }),
    ("Part mapping uses the composite BaseProduct and PartID", () =>
    {
        var screws = store.Explore(new() { BaseParts = ["BP-SCREW"] }).Summary;
        Equal(2, screws.Requests); Equal(9m, screws.Units); Equal(4m, screws.FulfilledUnits);
        var bolts = store.Explore(new() { BaseParts = ["BP-BOLT"] }).Summary;
        Equal(1, bolts.Requests); Equal(3m, bolts.Units); Equal(0, bolts.FulfilledRequests);
    }),
    ("Request BaseProduct takes precedence over the current product catalogue", () =>
    {
        var result = store.GetRequests(new() { BaseParts = ["BP-HIST"] });
        Equal(1, result.TotalRows); Equal("R5", result.Rows[0].Key);
        Equal("B-HIST", result.Rows[0].BaseProduct); Equal(6m, result.Rows[0].Units);
    }),
    ("Catalogue BaseProduct fallback works when request column is omitted", () =>
    {
        using var data = new Fixture();
        string path = Path.Combine(data.Imports, "CustomerRequests.csv");
        string[] lines = File.ReadAllLines(path);
        File.WriteAllLines(path, lines.Select(line => string.Join(',', line.Split(',').Take(6))));
        var fallback = data.CreateStore(); fallback.ReplaceSnapshot(SparesStore.LoadDirectory(data.Imports));
        var screws = fallback.Explore(new() { BaseParts = ["BP-SCREW"] }).Summary;
        Equal(3, screws.Requests); Equal(15m, screws.Units);
    }),
    ("Selecting multiple parts counts a matching request once", () =>
    {
        var result = store.Explore(new() { BaseParts = ["BP-SCREW", "BP-PANEL"], GroupBy = "basePart" });
        Equal(3, result.Summary.Requests); Equal(14m, result.Summary.Units);
        Equal(4, result.Rows.Sum(r => r.Requests));
        Equal(14m, result.Rows.Sum(r => r.Units));
    }),
    ("Repeated lines and zero-quantity lines are preserved", () =>
    {
        var details = store.GetRequests(new() { Search = "R1" });
        Equal(1, details.TotalRows); Equal(4, details.Rows[0].Lines); Equal(5m, details.Rows[0].Units);
        Equal(2, details.Rows[0].Parts.Count(p => p.PartId == "01"));
        var zero = store.Explore(new() { BaseParts = ["BP-ZERO"] }).Summary;
        Equal(1, zero.Requests); Equal(1, zero.Lines); Equal(0m, zero.Units); Equal(0m, zero.UnitFulfilment);
        Check(store.GetMetadata().Diagnostics.Any(d => d.Code == "identical-part-rows" && d.Count == 1));
    }),
    ("Missing part lines do not remove requests from factory totals", () =>
    {
        var alpha = store.Explore(new() { Factories = ["Factory Alpha"] }).Summary;
        Equal(4, alpha.Requests); Equal(2, alpha.FulfilledRequests); Equal(15m, alpha.Units);
        Equal(1, alpha.NoPartRequests);
    }),
    ("Missing lookups and unmapped parts remain in demand", () =>
    {
        var historic = store.Explore(new() { Factories = ["Historic Factory"] }).Summary;
        Equal(2, historic.Requests); Equal(5m, historic.Units);
        var unknown = store.Explore(new() { Factories = ["Unknown"], GroupBy = "basePart" });
        Equal(1, unknown.Summary.Requests); Equal(7m, unknown.Summary.Units);
        Check(unknown.Rows[0].Key.StartsWith("Unmapped:", StringComparison.Ordinal));
        Equal(7m, store.Explore(new() { BaseParts = [unknown.Rows[0].Key] }).Summary.Units);
    }),
    ("Known unquoted-comma layouts are repaired and counted", () =>
    {
        var meta = store.GetMetadata();
        Equal(3, meta.SourceFiles.Sum(f => f.RepairedRows));
        var chair = store.Explore(new() { Products = ["P1"], GroupBy = "product" }).Rows.Single();
        Equal("Chair, blue", chair.Label);
        var screw = store.Explore(new() { BaseParts = ["BP-SCREW"], GroupBy = "basePart" }).Rows.Single();
        Equal("Screw, zinc", screw.Label);
        var maker = store.Explore(new() { Manufacturers = ["Maker, Two"] }).Summary;
        Equal(1, maker.Requests); Equal(3m, maker.Units);
    }),
    ("Quoted commas, escaped quotes and multiline fields parse correctly", () =>
    {
        var parsed = DelimitedText.Parse("a,b\r\n1,\"hello, \"\"world\"\"\r\nnext line\"\r\n");
        Equal(2, parsed.Count); Equal("hello, \"world\"\r\nnext line", parsed[1][1]);
        var desk = store.Explore(new() { Products = ["P2"], GroupBy = "product" }).Rows.Single();
        Equal("Desk, \"wide\"", desk.Label);
    }),
    ("UK dates and inclusive ISO filter boundaries", () =>
    {
        Equal("2025-02-01", store.GetMetadata().RequestDateFrom);
        var result = store.Explore(new() { From = "2025-02-02", To = "2025-02-03" });
        Equal(2, result.Summary.Requests); Equal(7m, result.Summary.Units);
        Equal(0, result.Summary.FulfilledRequests);
    }),
    ("Despatch-date selection excludes missing dates without redefining fulfilment", () =>
    {
        var result = store.Explore(new() { DateBasis = "despatch", From = "2025-02-03", To = "2025-02-03" });
        Equal(2, result.Summary.Requests); Equal(0, result.Summary.FulfilledRequests);
        Equal(7m, result.Summary.Units);
        Check(store.GetMetadata().Diagnostics.Any(d => d.Code == "despatch-before-request" && d.Count == 1));
    }),
    ("Multiple selections use OR within a dimension and AND across dimensions", () =>
    {
        var result = store.Explore(new()
        {
            Products = ["P1", "P2"], Factories = ["factory alpha", "FACTORY BETA"],
            Statuses = ["NotSatisfied", "NotReported"], Customers = ["a"]
        });
        Equal(3, result.Summary.Requests); Equal(7m, result.Summary.Units);
    }),
    ("Facets exclude their own selection but respect other filters", () =>
    {
        var result = store.Explore(new() { Factories = ["Factory Alpha"] });
        Equal(4, result.Facets.Factories.Count);
        Check(result.Facets.Factories.Any(f => f.Value == "Factory Beta"));
        Equal(1, result.Facets.Products.Count); Equal("P1", result.Facets.Products[0].Value);
        var parts = store.Explore(new() { BaseParts = ["BP-SCREW"] });
        Check(parts.Facets.BaseParts.Any(p => p.Value == "BP-BOLT"));
        Equal(2, parts.Facets.Products.Count);
    }),
    ("Part search narrows units and request search preserves a matching request", () =>
    {
        var partSearch = store.Explore(new() { Search = "bp-screw" }).Summary;
        Equal(2, partSearch.Requests); Equal(9m, partSearch.Units);
        var productSearch = store.Explore(new() { Search = "chair" }).Summary;
        Equal(4, productSearch.Requests); Equal(15m, productSearch.Units);
        Equal(1, productSearch.NoPartRequests);
    }),
    ("Week grouping starts on Monday and empty days are represented", () =>
    {
        var result = store.Explore(new() { TrendBucket = "week" });
        Equal(2, result.Trend.Count); Equal("2025-01-27", result.Trend[0].Date);
        Equal("w/c 27/01/2025", result.Trend[0].Label);
        Equal(2, result.Trend[0].Requests); Equal(6, result.Trend[1].Requests);
        var gaps = store.Explore(new() { BaseParts = ["BP-SCREW"], TrendBucket = "day" });
        Equal(4, gaps.Trend.Count); Equal(0, gaps.Trend[1].Requests);
        Equal(9m, gaps.Trend.Sum(p => p.Units));
    }),
    ("Metric-aware sorting and pagination use the complete group set", () =>
    {
        var result = store.Explore(new() { Metric = "units", PageSize = 2 });
        Equal(4, result.TotalRows); Equal(2, result.TotalPages); Equal(2, result.Rows.Count);
        Equal("Factory Alpha", result.Rows[0].Key); Equal(15m, result.Rows[0].Demand);
        Equal("Unknown", result.Rows[1].Key);
        var last = store.Explore(new() { Metric = "units", PageSize = 2, Page = 99 });
        Equal(2, last.Page); Equal(2, last.Rows.Count);
        var names = store.Explore(new() { Sort = "name", Direction = "asc" });
        Equal("Factory Alpha", names.Rows[0].Key); Equal("Factory Beta", names.Rows[1].Key);
    }),
    ("Request details retain leading zero PartIDs and show only matching lines", () =>
    {
        var result = store.GetRequests(new() { BaseParts = ["BP-SCREW"], PageSize = 1 });
        Equal(2, result.TotalRows); Equal(2, result.TotalPages); Equal(1, result.Rows.Count);
        Equal("R4", result.Rows[0].Key); Equal("01", result.Rows[0].Parts[0].PartId);
        Equal(5m, result.Rows[0].Units);
        var r1 = store.GetRequests(new() { BaseParts = ["BP-SCREW"], PageSize = 1, Page = 2 }).Rows[0];
        Equal(2, r1.Parts.Count); Equal(4m, r1.Units);
    }),
    ("Repeated manufacturer names never multiply requests", () =>
    {
        var summary = store.Explore(new() { Manufacturers = ["Maker One"] }).Summary;
        Equal(4, summary.Requests); Equal(15m, summary.Units);
        Check(store.GetMetadata().Diagnostics.Any(d => d.Code == "duplicate-manufacturer-names" && d.Count == 1));
    }),
    ("Invalid filters fail explicitly", () =>
    {
        Throws<ArgumentException>(() => store.Explore(new() { From = "01/02/2025" }));
        Throws<ArgumentException>(() => store.Explore(new() { From = "2025-03-01", To = "2025-02-01" }));
        Throws<ArgumentException>(() => store.Explore(new() { GroupBy = "arbitrary" }));
        Throws<ArgumentException>(() => store.Explore(new() { Outcome = "pending" }));
    }),
    ("An invalid reload preserves the last valid snapshot", () =>
    {
        using var data = new Fixture(); var reload = data.CreateStore();
        reload.ReloadAsync().GetAwaiter().GetResult();
        string version = reload.GetMetadata().Version;
        File.AppendAllText(Path.Combine(data.Imports, "CustomerRequests.csv"), "Rubbish\n");
        Throws<InvalidDataException>(() => reload.ReloadAsync().GetAwaiter().GetResult());
        Equal(version, reload.GetMetadata().Version); Equal(8, reload.Explore(new()).Summary.Requests);
    }),
    ("A no-op reload preserves the content version and load time", () =>
    {
        using var data = new Fixture(); var reload = data.CreateStore();
        reload.ReloadAsync().GetAwaiter().GetResult();
        var original = reload.GetMetadata();
        reload.ReloadAsync().GetAwaiter().GetResult();
        Equal(original.Version, reload.GetMetadata().Version); Equal(original.LoadedAt, reload.GetMetadata().LoadedAt);
    }),
    ("Duplicate primary keys, orphan lines and partial snapshots are rejected", () =>
    {
        using var duplicate = new Fixture();
        File.AppendAllText(Path.Combine(duplicate.Imports, "parts.csv"), "B1,01,1,OTHER\n");
        Throws<InvalidDataException>(() => SparesStore.LoadDirectory(duplicate.Imports));
        using var orphan = new Fixture();
        File.AppendAllText(Path.Combine(orphan.Imports, "custreqparts.csv"), "NO_REQUEST,01,1\n");
        Throws<InvalidDataException>(() => SparesStore.LoadDirectory(orphan.Imports));
        using var partial = new Fixture();
        File.Delete(Path.Combine(partial.Imports, "products.csv"));
        Throws<InvalidDataException>(() => SparesStore.LoadDirectory(partial.Imports));
    }),
    ("Malformed CSV is rejected outside the three explicitly repairable schemas", () =>
    {
        using var data = new Fixture();
        File.AppendAllText(Path.Combine(data.Imports, "custreqparts.csv"), "R1,01,1,extra\n");
        Throws<InvalidDataException>(() => SparesStore.LoadDirectory(data.Imports));
        Throws<InvalidDataException>(() => DelimitedText.Parse("a,b\n1,\"not closed"));
        Throws<InvalidDataException>(() => DelimitedText.Parse("a,b\n1,\"closed\"junk\n"));
    }),
    ("An empty import directory has a useful empty result", () =>
    {
        using var data = new Fixture();
        foreach (string file in Directory.GetFiles(data.Imports)) File.Delete(file);
        var empty = data.CreateStore(); empty.ReloadAsync().GetAwaiter().GetResult();
        Check(!empty.GetMetadata().HasData); Equal(0, empty.Explore(new()).Summary.Requests);
        Equal(0, empty.Explore(new()).Rows.Count); Equal(1, empty.Explore(new()).TotalPages);
    })
};

foreach (var (name, run) in cases)
{
    try { run(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); return 1; }
}

int dataArg = Array.IndexOf(args, "--data");
if (dataArg >= 0)
{
    if (dataArg + 1 >= args.Length) { Console.Error.WriteLine("--data requires a CSV directory."); return 2; }
    string directory = Path.GetFullPath(args[dataArg + 1]);
    Snapshot snapshot = SparesStore.LoadDirectory(directory);
    var check = fixture.CreateStore(); check.ReplaceSnapshot(snapshot);
    var summary = check.Explore(new()).Summary;
    string requestsFile = Directory.GetFiles(directory).Single(p => Path.GetFileName(p).Equals("CustomerRequests.csv", StringComparison.OrdinalIgnoreCase));
    string partsFile = Directory.GetFiles(directory).Single(p => Path.GetFileName(p).Equals("custreqparts.csv", StringComparison.OrdinalIgnoreCase));
    var requestRows = DelimitedText.Read(requestsFile, "customerrequests").Rows;
    var lineRows = DelimitedText.Read(partsFile, "custreqparts").Rows;
    var statusByRequest = requestRows.ToDictionary(r => r["Key"], r => r["Satisfied"], StringComparer.OrdinalIgnoreCase);
    bool Fulfilled(string status) => status.Equals("Satisfied", StringComparison.OrdinalIgnoreCase) || status.Equals("Satified", StringComparison.OrdinalIgnoreCase);
    Equal(requestRows.Count, summary.Requests); Equal(lineRows.Count, summary.Lines);
    Equal(requestRows.Count(r => Fulfilled(r["Satisfied"])), summary.FulfilledRequests);
    Equal(lineRows.Sum(r => decimal.Parse(r["Qty"], CultureInfo.InvariantCulture)), summary.Units);
    Equal(lineRows.Where(r => Fulfilled(statusByRequest[r["CR_Key"]])).Sum(r => decimal.Parse(r["Qty"], CultureInfo.InvariantCulture)), summary.FulfilledUnits);
    Equal(lineRows.Count(r => Fulfilled(statusByRequest[r["CR_Key"]])), summary.FulfilledLines);
    var keysWithLines = lineRows.Select(r => r["CR_Key"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
    Equal(requestRows.Count(r => !keysWithLines.Contains(r["Key"])), summary.NoPartRequests);
    Equal(summary.Requests, check.Explore(new() { TrendBucket = "month" }).Trend.Sum(t => t.Requests));
    Equal(summary.Units, check.Explore(new() { TrendBucket = "month" }).Trend.Sum(t => t.Units));
    Console.WriteLine("PASS Local dataset request, status, line, quantity and trend totals reconcile independently.");
    passed++;
}

Console.WriteLine($"{passed} checks passed.");
return 0;

static void Check(bool condition)
{
    if (!condition) throw new Exception("Condition was false.");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}.");
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

sealed class Fixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "spares-self-tests-" + Guid.NewGuid().ToString("N"));
    public string Imports => Path.Combine(_root, "Data", "Imports");

    public Fixture()
    {
        Directory.CreateDirectory(Imports);
        Write("CustomerRequests.csv", """
            Date,Key,Product,Satisfied,DespatchDate,customer,BaseProduct
            01/02/2025,R1,P1,Satisfied,,A,
            02/02/2025,R2,P2,NotSatisfied,03/02/2025 09:10:11,A,
            03/02/2025,R3,P1,NotReported,03/02/2025,A,
            04/02/2025,R4,P3,Processing,,B,
            05/02/2025,R5,P1,Satified,,A,B-HIST
            06/02/2025,R6,P3,Satisfied,05/02/2025,A,
            07/02/2025,R7,P1,NotSatisfied,07/02/2025,A,
            08/02/2025,R8,P404,Paused,,A,
            """);
        Write("custreqparts.csv", """
            CR_Key,PartID,Qty
            R1,01,2
            R1,01,2
            R1,02,1
            R1,Z,0
            R2,01,3
            R3,02,4
            R4,01,5
            R5,01,6
            R8,99,7
            """);
        Write("products.csv", """"
            Catalogue,Product,BaseProduct,Description,Manufacturer,Factory
            CAT1,P1,B1,Chair, blue,Maker One,Factory Alpha
            CAT2,P2,B2,"Desk, ""wide""","Maker, Two",Factory Beta
            CAT3,P3,B3,Bench,Historic Maker,Historic Factory
            CAT4,P4,B3,Bench variation,Historic Maker,Historic Factory
            """");
        Write("parts.csv", """
            Product,PartID,Qty_Rqd,BasePart
            B1,01,8,BP-SCREW
            B1,02,1,BP-PANEL
            B1,Z,1,BP-ZERO
            B2,01,20,BP-BOLT
            B3,01,8,BP-SCREW
            B-HIST,01,30,BP-HIST
            """);
        Write("baseparts.csv", """
            BasePart,Description
            BP-SCREW,Screw, zinc
            BP-PANEL,Side panel
            BP-ZERO,Zero quantity item
            BP-BOLT,Bolt
            BP-HIST,Historic fitting
            """);
        Write("manufacturers.csv", """
            ManufacturerCode,Manufacturer,ChargeTo
            M1,Maker One,Example retailer
            M2,Maker, Two,Example retailer
            M3,Maker One,Example retailer
            """);
        Write("factories.csv", """
            FactoryID,FactoryName,Location,Retailer,Current
            F1,Factory Alpha,UK,A,-1
            F2,Factory Beta,China,A,-1
            """);
    }

    public SparesStore CreateStore() => new(new FakeEnvironment(_root), NullLogger<SparesStore>.Instance);
    private void Write(string name, string text) => File.WriteAllText(Path.Combine(Imports, name), text + "\n", new UTF8Encoding(false));
    public void Dispose() => Directory.Delete(_root, true);
}

sealed class FakeEnvironment(string root) : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "SparesSelfTests";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = root;
    public string EnvironmentName { get; set; } = "Testing";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
