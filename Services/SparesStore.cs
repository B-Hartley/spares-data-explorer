using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ClickSpares.SparesExplorer.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace ClickSpares.SparesExplorer.Services;

/// <summary>
/// Publishes complete immutable snapshots. Queries read one snapshot, so a concurrent import
/// cannot combine old request rows with new line rows or partially replace a result.
/// </summary>
public sealed class SparesStore
{
    private static readonly CultureInfo Uk = CultureInfo.GetCultureInfo("en-GB");
    private static readonly StringComparer Keys = StringComparer.OrdinalIgnoreCase;
    private static readonly string[] RequiredTables =
        ["customerrequests", "custreqparts", "products", "parts", "baseparts", "manufacturers", "factories"];

    private readonly string _importDirectory;
    private readonly ILogger<SparesStore> _logger;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private Snapshot _snapshot = EmptySnapshot();

    public SparesStore(IWebHostEnvironment environment, ILogger<SparesStore> logger)
    {
        _importDirectory = Path.Combine(environment.ContentRootPath, "Data", "Imports");
        _logger = logger;
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _reloadLock.WaitAsync(cancellationToken);
        try
        {
            Snapshot next = await Task.Run(() => LoadDirectory(_importDirectory), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Snapshot previous = Volatile.Read(ref _snapshot);
            if (next.Metadata.Version == "empty" && previous.Metadata.HasData)
                throw new InvalidDataException("The import folder is empty. The last valid dataset has been retained.");
            if (next.Metadata.Version != previous.Metadata.Version)
            {
                ReplaceSnapshot(next);
                _logger.LogInformation("Loaded spare-parts dataset: {Requests} requests and {Lines} part lines.",
                    next.Metadata.RequestCount, next.Metadata.LineCount);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The dataset could not be refreshed; the previous complete snapshot remains available.");
            throw;
        }
        finally { _reloadLock.Release(); }
    }

    public void ReplaceSnapshot(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _snapshot, snapshot);
    }

    public Metadata GetMetadata() => Volatile.Read(ref _snapshot).Metadata;

    public static Snapshot LoadDirectory(string path)
    {
        if (!Directory.Exists(path)) return EmptySnapshot();
        string[] files = Directory.GetFiles(path).Where(p => Path.GetExtension(p).Equals(".csv", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (files.Length == 0) return EmptySnapshot();

        var paths = new Dictionary<string, string>(Keys);
        foreach (string file in files)
        {
            string name = Path.GetFileNameWithoutExtension(file).Replace("_", "").Replace(" ", "").ToLowerInvariant();
            if (!RequiredTables.Contains(name))
                throw new InvalidDataException($"Unexpected CSV file {Path.GetFileName(file)}. Import the seven named table snapshots together.");
            if (!paths.TryAdd(name, file))
                throw new InvalidDataException($"More than one CSV was supplied for {name}. Import one complete snapshot per table.");
        }
        string[] missing = RequiredTables.Where(n => !paths.ContainsKey(n)).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException($"The dataset is incomplete. Missing CSV file(s): {string.Join(", ", missing.Select(n => n + ".csv"))}.");

        var tables = RequiredTables.ToDictionary(n => n, n => DelimitedText.Read(paths[n], n));
        var diagnostics = tables.Values.SelectMany(t => t.Diagnostics).ToList();
        CsvTable customerRequests = tables["customerrequests"], requestParts = tables["custreqparts"],
            products = tables["products"], parts = tables["parts"], baseParts = tables["baseparts"],
            manufacturers = tables["manufacturers"], factories = tables["factories"];

        customerRequests.Require("Date", "Key", "Product", "DespatchDate", "customer");
        string statusColumn = customerRequests.Headers.Contains("Satisfied", Keys) ? "Satisfied" : "Satified";
        customerRequests.Require(statusColumn);
        requestParts.Require("CR_Key", "PartID", "Qty");
        products.Require("Catalogue", "Product", "BaseProduct", "Description", "Manufacturer", "Factory");
        parts.Require("Product", "PartID", "Qty_Rqd", "BasePart");
        baseParts.Require("BasePart", "Description");
        manufacturers.Require("ManufacturerCode", "Manufacturer", "ChargeTo");
        factories.Require("FactoryID", "FactoryName", "Location", "Retailer", "Current");

        var manufacturerByCode = UniqueRows(manufacturers, "ManufacturerCode");
        var factoryById = UniqueRows(factories, "FactoryID");
        var manufacturerByName = manufacturers.Rows.GroupBy(r => r["Manufacturer"], Keys).ToDictionary(g => g.Key, g => g.ToArray(), Keys);
        var factoryByName = factories.Rows.GroupBy(r => r["FactoryName"], Keys).ToDictionary(g => g.Key, g => g.ToArray(), Keys);
        var basePartByCode = UniqueRows(baseParts, "BasePart");
        var productRows = UniqueRows(products, "Product");

        int ambiguousManufacturerNames = manufacturerByName.Count(x => x.Value.Length > 1);
        AddDiagnostic(diagnostics, "info", "duplicate-manufacturer-names",
            "Manufacturer names with multiple codes are grouped by their supplied name; requests are never multiplied by that lookup.", ambiguousManufacturerNames);

        var productByCode = new Dictionary<string, ProductDefinition>(Keys);
        foreach (var row in productRows.Values)
        {
            string manufacturer = Nonblank(row["Manufacturer"], "Unknown"), factory = Nonblank(row["Factory"], "Unknown");
            bool matchedManufacturer = manufacturerByCode.TryGetValue(manufacturer, out var manufacturerRow);
            if (matchedManufacturer) manufacturer = Nonblank(manufacturerRow!["Manufacturer"], manufacturer);
            else matchedManufacturer = manufacturerByName.ContainsKey(manufacturer);

            bool matchedFactory = factoryById.TryGetValue(factory, out var factoryRow);
            if (matchedFactory) factory = Nonblank(factoryRow!["FactoryName"], factory);
            else if (factoryByName.TryGetValue(factory, out var factoryMatches))
            {
                matchedFactory = true;
                factoryRow = factoryMatches.Length == 1 ? factoryMatches[0] : null;
            }
            productByCode.Add(row["Product"], new ProductDefinition(
                row["Product"], row["BaseProduct"], Nonblank(row["Description"], row["Product"]),
                manufacturer, factory, factoryRow?["Location"] ?? "", matchedManufacturer, matchedFactory));
        }

        var partByComposite = new Dictionary<(string, string), CsvRow>();
        foreach (var row in parts.Rows)
        {
            string product = row.Required("Product", parts.Name), partId = row.Required("PartID", parts.Name);
            row.Required("BasePart", parts.Name);
            if (!partByComposite.TryAdd((Normalise(product), Normalise(partId)), row))
                throw new InvalidDataException($"{parts.Name}, row {row.Number}: Product and PartID must form a unique mapping.");
        }

        var requestByKey = new Dictionary<string, RequestBuilder>(Keys);
        int missingProducts = 0, missingManufacturers = 0, missingFactories = 0,
            directBaseProducts = 0, blankFulfilledDates = 0, datedUnfulfilled = 0, negativeDates = 0, unknownStatuses = 0;
        foreach (var row in customerRequests.Rows)
        {
            string key = row.Required("Key", customerRequests.Name), productCode = row.Required("Product", customerRequests.Name);
            DateOnly date = ParseDataDate(row.Required("Date", customerRequests.Name), customerRequests.Name, row.Number, "Date");
            DateOnly? despatchDate = string.IsNullOrWhiteSpace(row["DespatchDate"]) ? null :
                ParseDataDate(row["DespatchDate"], customerRequests.Name, row.Number, "DespatchDate");
            string status = Nonblank(row[statusColumn], "Unknown");
            bool fulfilled = IsFulfilled(status);
            bool matchedProduct = productByCode.TryGetValue(productCode, out var product);
            if (!matchedProduct) missingProducts++;
            if (product is not null && !product.ManufacturerMatched) missingManufacturers++;
            if (product is not null && !product.FactoryMatched) missingFactories++;
            string baseProduct = row["BaseProduct"];
            if (!string.IsNullOrWhiteSpace(baseProduct)) directBaseProducts++;
            else baseProduct = product?.BaseProduct ?? "";
            if (fulfilled && !despatchDate.HasValue) blankFulfilledDates++;
            if (!fulfilled && despatchDate.HasValue) datedUnfulfilled++;
            if (despatchDate.HasValue && despatchDate.Value < date) negativeDates++;
            if (!new[] { "Satisfied", "Satified", "NotSatisfied", "NotReported", "NotReports", "Processing" }.Contains(status, Keys)) unknownStatuses++;

            var request = new RequestBuilder(key, date, despatchDate, productCode,
                product?.Description ?? productCode, baseProduct, product?.Manufacturer ?? "Unknown",
                product?.Factory ?? "Unknown", Nonblank(row["customer"], "Unknown"), status,
                fulfilled, product?.FactoryLocation ?? "");
            if (!requestByKey.TryAdd(key, request))
                throw new InvalidDataException($"{customerRequests.Name}, row {row.Number}: request Key must be unique within the snapshot.");
        }

        int repeatedPartRows = 0, identicalPartRows = 0, missingPartMappings = 0, missingPartDescriptions = 0, zeroQuantities = 0;
        var seenRequestParts = new HashSet<(string, string)>();
        var seenExactLines = new HashSet<(string, string, decimal)>();
        foreach (var row in requestParts.Rows)
        {
            string key = row.Required("CR_Key", requestParts.Name), partId = row.Required("PartID", requestParts.Name);
            if (!requestByKey.TryGetValue(key, out var request))
                throw new InvalidDataException($"{requestParts.Name}, row {row.Number}: CR_Key has no matching customer request. Supply matching complete table snapshots.");
            if (!decimal.TryParse(row.Required("Qty", requestParts.Name), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal quantity) || quantity < 0)
                throw new InvalidDataException($"{requestParts.Name}, row {row.Number}: Qty must be a non-negative number.");
            if (quantity == 0) zeroQuantities++;
            var requestPartKey = (Normalise(key), Normalise(partId));
            if (!seenRequestParts.Add(requestPartKey)) repeatedPartRows++;
            if (!seenExactLines.Add((requestPartKey.Item1, requestPartKey.Item2, quantity))) identicalPartRows++;

            string basePart, description;
            if (partByComposite.TryGetValue((Normalise(request.BaseProduct), Normalise(partId)), out var mapping))
            {
                basePart = mapping["BasePart"];
                if (basePartByCode.TryGetValue(basePart, out var basePartRow))
                    description = Nonblank(basePartRow["Description"], basePart);
                else
                {
                    description = "Description unavailable";
                    missingPartDescriptions++;
                }
            }
            else
            {
                basePart = $"Unmapped: {Nonblank(request.BaseProduct, request.Product)} / {partId}";
                description = "No matching product-part mapping";
                missingPartMappings++;
            }
            request.Parts.Add(new PartFact(partId, basePart, description, quantity));
        }

        RequestFact[] facts = requestByKey.Values.Select(r => r.ToFact()).ToArray();
        AddDiagnostic(diagnostics, "warning", "missing-product", "Requests whose product is absent from products.csv are retained under Unknown manufacturer and factory.", missingProducts);
        AddDiagnostic(diagnostics, "info", "missing-manufacturer-lookup", "Requests use manufacturer names absent from manufacturers.csv. Their supplied product manufacturer names are retained.", missingManufacturers);
        AddDiagnostic(diagnostics, "info", "missing-factory-lookup", "Requests use factory names absent from factories.csv. Their supplied product factory names are retained.", missingFactories);
        AddDiagnostic(diagnostics, "warning", "missing-part-mapping", "Part lines without a product/PartID mapping are retained as Unmapped parts.", missingPartMappings);
        AddDiagnostic(diagnostics, "info", "missing-part-description", "Part lines whose BasePart is absent from baseparts.csv retain their BasePart identifier.", missingPartDescriptions);
        AddDiagnostic(diagnostics, "info", "requests-without-parts", "Requests with no recorded part lines remain in request totals, with zero part units.", facts.Count(r => r.Parts.Length == 0));
        AddDiagnostic(diagnostics, "info", "repeated-request-parts", "Repeated request/PartID rows are retained as supplied. They cannot safely be deduplicated without a line identifier.", repeatedPartRows);
        AddDiagnostic(diagnostics, "info", "identical-part-rows", "Exact repeated request/PartID/Qty rows are retained within the snapshot.", identicalPartRows);
        AddDiagnostic(diagnostics, "info", "zero-quantity-lines", "Zero-quantity part lines are retained; they contribute no part units.", zeroQuantities);
        AddDiagnostic(diagnostics, "info", "fulfilled-without-date", "Fulfilled requests without a despatch date remain fulfilled according to Satisfied.", blankFulfilledDates);
        AddDiagnostic(diagnostics, "info", "unfulfilled-with-date", "Not-fulfilled requests with a despatch date remain not fulfilled according to Satisfied.", datedUnfulfilled);
        AddDiagnostic(diagnostics, "warning", "despatch-before-request", "Some despatch dates precede the request date. These dates are retained; no lead-time metric is inferred.", negativeDates);
        AddDiagnostic(diagnostics, "info", "unknown-status", "Additional or blank status values are treated as not fulfilled.", unknownStatuses);
        AddDiagnostic(diagnostics, "info", "request-baseproduct", "Request-specific BaseProduct values take precedence over the product catalogue mapping.", directBaseProducts);
        if (facts.Length == 0) diagnostics.Add(new("info", "no-requests", "The imported snapshot contains no requests."));

        DateOnly[] despatchDates = facts.Where(r => r.DespatchDate.HasValue).Select(r => r.DespatchDate!.Value).ToArray();
        string versionInput = "spares-snapshot-v1\n" + string.Join('\n', RequiredTables.Select(n => n + ":" + tables[n].Hash));
        var metadata = new Metadata
        {
            Version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(versionInput))).ToLowerInvariant(),
            LoadedAt = DateTimeOffset.UtcNow,
            HasData = facts.Length > 0,
            RequestCount = facts.Length,
            LineCount = requestParts.Rows.Count,
            RequestDateFrom = facts.Length == 0 ? null : Iso(facts.Min(r => r.Date)),
            RequestDateTo = facts.Length == 0 ? null : Iso(facts.Max(r => r.Date)),
            DespatchDateFrom = despatchDates.Length == 0 ? null : Iso(despatchDates.Min()),
            DespatchDateTo = despatchDates.Length == 0 ? null : Iso(despatchDates.Max()),
            SourceFiles = Array.AsReadOnly(RequiredTables.Select(n => new SourceFileInfo(tables[n].Name, tables[n].Rows.Count, tables[n].RepairedRows)).ToArray()),
            Diagnostics = diagnostics.AsReadOnly()
        };
        return new Snapshot(facts, metadata);
    }

    public ExploreResponse Explore(ExploreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Snapshot snapshot = Volatile.Read(ref _snapshot);
        var filter = new PreparedFilter(request);
        List<MatchedRequest> matches = Filter(snapshot, filter);
        AggregateCounts counts = Count(matches);
        var allParts = matches.SelectMany(m => m.Parts);
        var summary = new ExplorerSummary
        {
            Requests = counts.Requests, FulfilledRequests = counts.FulfilledRequests,
            Units = counts.Units, FulfilledUnits = counts.FulfilledUnits,
            Lines = counts.Lines, FulfilledLines = counts.FulfilledLines,
            Manufacturers = matches.Select(m => m.Request.Manufacturer).Distinct(Keys).Count(),
            Factories = matches.Select(m => m.Request.Factory).Distinct(Keys).Count(),
            Products = matches.Select(m => m.Request.Product).Distinct(Keys).Count(),
            BaseParts = allParts.Select(p => p.BasePart).Distinct(Keys).Count(),
            NoPartRequests = matches.Count(m => m.Request.Parts.Length == 0)
        };

        List<GroupRow> groups = BuildGroups(matches, filter);
        int totalPages = Math.Max(1, (groups.Count + filter.PageSize - 1) / filter.PageSize);
        int page = Math.Clamp(filter.Page, 1, totalPages);
        return new ExploreResponse
        {
            Summary = summary,
            Rows = SortGroups(groups, filter).Skip((page - 1) * filter.PageSize).Take(filter.PageSize).ToArray(),
            Trend = BuildTrend(matches, filter),
            Statuses = matches.GroupBy(m => m.Request.Status, Keys).Select(g => ToStatus(g.Key, Count(g))).OrderBy(s => s.Key, Keys).ToArray(),
            Facets = new ExplorerFacets
            {
                Manufacturers = BuildFacet(Filter(snapshot, filter, "manufacturer"), "manufacturer"),
                Factories = BuildFacet(Filter(snapshot, filter, "factory"), "factory"),
                Products = BuildFacet(Filter(snapshot, filter, "product"), "product"),
                BaseParts = BuildFacet(Filter(snapshot, filter, "basepart"), "basepart"),
                Statuses = BuildFacet(Filter(snapshot, filter, "status"), "status"),
                Customers = BuildFacet(Filter(snapshot, filter, "customer"), "customer")
            },
            Metadata = snapshot.Metadata,
            GroupBy = filter.GroupBy == "basepart" ? "basePart" : filter.GroupBy,
            Metric = filter.Metric, DateBasis = filter.DateBasis, Sort = filter.Sort,
            Direction = filter.Direction, TrendBucket = filter.TrendBucket,
            Page = page, PageSize = filter.PageSize, TotalRows = groups.Count, TotalPages = totalPages
        };
    }

    public RequestDetailsResponse GetRequests(ExploreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Snapshot snapshot = Volatile.Read(ref _snapshot);
        var filter = new PreparedFilter(request);
        List<MatchedRequest> matches = Filter(snapshot, filter);
        int totalPages = Math.Max(1, (matches.Count + filter.PageSize - 1) / filter.PageSize);
        int page = Math.Clamp(filter.Page, 1, totalPages);
        return new RequestDetailsResponse
        {
            Rows = matches.OrderByDescending(m => BasisDate(m.Request, filter.DateBasis)).ThenBy(m => m.Request.Key, Keys)
                .Skip((page - 1) * filter.PageSize).Take(filter.PageSize).Select(m => new RequestRecord
                {
                    Key = m.Request.Key, Date = Iso(m.Request.Date),
                    DespatchDate = m.Request.DespatchDate.HasValue ? Iso(m.Request.DespatchDate.Value) : null,
                    Product = m.Request.Product, ProductName = m.Request.ProductName,
                    BaseProduct = m.Request.BaseProduct, Manufacturer = m.Request.Manufacturer,
                    Factory = m.Request.Factory, Customer = m.Request.Customer, Status = m.Request.Status,
                    Fulfilled = m.Request.Fulfilled, Units = m.Parts.Sum(p => p.Quantity), Lines = m.Parts.Length,
                    Parts = m.Parts.Select(p => new RequestPart(p.PartId, p.BasePart, p.Description, p.Quantity)).ToArray()
                }).ToArray(),
            Page = page, PageSize = filter.PageSize, TotalRows = matches.Count, TotalPages = totalPages
        };
    }

    private static List<MatchedRequest> Filter(Snapshot snapshot, PreparedFilter filter, string? excluding = null)
    {
        var matches = new List<MatchedRequest>();
        foreach (var request in snapshot.Requests)
        {
            DateOnly? date = BasisDate(request, filter.DateBasis);
            if (!date.HasValue || filter.From.HasValue && date < filter.From || filter.To.HasValue && date > filter.To) continue;
            if (filter.Outcome == "fulfilled" && !request.Fulfilled || filter.Outcome == "unfulfilled" && request.Fulfilled) continue;
            if (excluding != "manufacturer" && !Allows(filter.Manufacturers, request.Manufacturer)) continue;
            if (excluding != "factory" && !Allows(filter.Factories, request.Factory)) continue;
            if (excluding != "product" && !Allows(filter.Products, request.Product)) continue;
            if (excluding != "status" && !Allows(filter.Statuses, request.Status)) continue;
            if (excluding != "customer" && !Allows(filter.Customers, request.Customer)) continue;

            bool hasPartFilter = excluding != "basepart" && filter.BaseParts.Count > 0;
            bool requestSearch = filter.Search.Length == 0 || RequestMatchesSearch(request, filter.Search);
            if (!hasPartFilter && requestSearch) { matches.Add(new(request, request.Parts)); continue; }
            PartFact[] matchingParts = request.Parts.Where(p => (!hasPartFilter || filter.BaseParts.Contains(p.BasePart)) &&
                (requestSearch || PartMatchesSearch(p, filter.Search))).ToArray();
            if (matchingParts.Length > 0) matches.Add(new(request, matchingParts));
        }
        return matches;
    }

    private static bool RequestMatchesSearch(RequestFact r, string search) =>
        Contains(r.Key, search) || Contains(r.Product, search) || Contains(r.ProductName, search) ||
        Contains(r.BaseProduct, search) || Contains(r.Manufacturer, search) || Contains(r.Factory, search) ||
        Contains(r.Customer, search) || Contains(r.Status, search);

    private static bool PartMatchesSearch(PartFact p, string search) =>
        Contains(p.PartId, search) || Contains(p.BasePart, search) || Contains(p.Description, search);

    private static bool Contains(string value, string search) => value.Contains(search, StringComparison.OrdinalIgnoreCase);
    private static bool Allows(HashSet<string> selection, string value) => selection.Count == 0 || selection.Contains(value);

    private static AggregateCounts Count(IEnumerable<MatchedRequest> matches)
    {
        int requests = 0, fulfilledRequests = 0, lines = 0, fulfilledLines = 0;
        decimal units = 0, fulfilledUnits = 0;
        foreach (var match in matches)
        {
            requests++;
            decimal requestUnits = match.Parts.Sum(p => p.Quantity);
            units += requestUnits;
            lines += match.Parts.Length;
            if (!match.Request.Fulfilled) continue;
            fulfilledRequests++;
            fulfilledUnits += requestUnits;
            fulfilledLines += match.Parts.Length;
        }
        return new AggregateCounts
        {
            Requests = requests, FulfilledRequests = fulfilledRequests, Units = units,
            FulfilledUnits = fulfilledUnits, Lines = lines, FulfilledLines = fulfilledLines
        };
    }

    private static List<GroupRow> BuildGroups(List<MatchedRequest> matches, PreparedFilter filter)
    {
        var grouped = new Dictionary<string, List<MatchedRequest>>(Keys);
        foreach (var match in matches)
        {
            if (filter.GroupBy == "basepart")
            {
                foreach (var parts in match.Parts.GroupBy(p => p.BasePart, Keys))
                {
                    if (!grouped.TryGetValue(parts.Key, out var bucket)) grouped[parts.Key] = bucket = [];
                    bucket.Add(new MatchedRequest(match.Request, parts.ToArray()));
                }
            }
            else
            {
                string key = filter.GroupBy switch
                {
                    "manufacturer" => match.Request.Manufacturer,
                    "product" => match.Request.Product,
                    _ => match.Request.Factory
                };
                if (!grouped.TryGetValue(key, out var bucket)) grouped[key] = bucket = [];
                bucket.Add(match);
            }
        }
        return grouped.Select(pair =>
        {
            AggregateCounts c = Count(pair.Value);
            MatchedRequest first = pair.Value[0];
            string label = filter.GroupBy switch
            {
                "product" => first.Request.ProductName,
                "basepart" => first.Parts[0].Description,
                _ => pair.Key
            };
            int productCount = pair.Value.Select(m => m.Request.Product).Distinct(Keys).Count();
            string subtitle = filter.GroupBy switch
            {
                "product" => pair.Key + " · " + first.Request.Factory,
                "basepart" => pair.Key,
                "manufacturer" => $"{pair.Value.Select(m => m.Request.Factory).Distinct(Keys).Count():N0} factories · {productCount:N0} products",
                _ => (first.Request.FactoryLocation.Length > 0 ? first.Request.FactoryLocation + " · " : "") + $"{productCount:N0} products"
            };
            return new GroupRow
            {
                Key = pair.Key, Label = label, Subtitle = subtitle,
                Requests = c.Requests, FulfilledRequests = c.FulfilledRequests, Units = c.Units,
                FulfilledUnits = c.FulfilledUnits, Lines = c.Lines, FulfilledLines = c.FulfilledLines,
                Demand = MetricDemand(c, filter.Metric), Unfulfilled = MetricUnfulfilled(c, filter.Metric),
                Fulfilment = MetricFulfilment(c, filter.Metric)
            };
        }).ToList();
    }

    private static IOrderedEnumerable<GroupRow> SortGroups(List<GroupRow> rows, PreparedFilter filter)
    {
        bool ascending = filter.Direction == "asc";
        IOrderedEnumerable<GroupRow> sorted;
        if (filter.Sort == "name") sorted = ascending ? rows.OrderBy(r => r.Label, Keys) : rows.OrderByDescending(r => r.Label, Keys);
        else
        {
            Func<GroupRow, decimal> selector = filter.Sort switch
            {
                "unfulfilled" => r => r.Unfulfilled,
                "fulfilment" => r => r.Fulfilment,
                _ => r => r.Demand
            };
            sorted = ascending ? rows.OrderBy(selector) : rows.OrderByDescending(selector);
        }
        return sorted.ThenBy(r => r.Label, Keys).ThenBy(r => r.Key, Keys);
    }

    private static IReadOnlyList<TrendPoint> BuildTrend(List<MatchedRequest> matches, PreparedFilter filter)
    {
        if (matches.Count == 0) return [];
        var byBucket = matches.GroupBy(m => BucketStart(BasisDate(m.Request, filter.DateBasis)!.Value, filter.TrendBucket))
            .ToDictionary(g => g.Key, g => Count(g));
        DateOnly first = byBucket.Keys.Min(), last = byBucket.Keys.Max();
        var result = new List<TrendPoint>();
        for (DateOnly date = first; date <= last; date = NextBucket(date, filter.TrendBucket))
        {
            if (result.Count >= 5000)
                throw new ArgumentException("This date range has more than 5,000 trend periods. Select weekly or monthly grouping, or a shorter date range.");
            AggregateCounts c = byBucket.GetValueOrDefault(date) ?? new AggregateCounts();
            string label = filter.TrendBucket switch
            {
                "month" => date.ToString("MMM yyyy", Uk),
                "week" => "w/c " + date.ToString("dd/MM/yyyy", Uk),
                _ => date.ToString("dd/MM/yyyy", Uk)
            };
            result.Add(new TrendPoint
            {
                Date = Iso(date), Label = label,
                Requests = c.Requests, FulfilledRequests = c.FulfilledRequests, Units = c.Units,
                FulfilledUnits = c.FulfilledUnits, Lines = c.Lines, FulfilledLines = c.FulfilledLines,
                Demand = MetricDemand(c, filter.Metric), Unfulfilled = MetricUnfulfilled(c, filter.Metric),
                Fulfilment = MetricFulfilment(c, filter.Metric)
            });
            if (date == last) break;
        }
        return result;
    }

    private static IReadOnlyList<FilterOption> BuildFacet(List<MatchedRequest> matches, string dimension)
    {
        var options = new Dictionary<string, (string Label, int Count)>(Keys);
        foreach (var match in matches)
        {
            if (dimension == "basepart")
            {
                foreach (var parts in match.Parts.GroupBy(p => p.BasePart, Keys))
                    AddOption(options, parts.Key, parts.Key + " · " + parts.First().Description);
            }
            else
            {
                var (key, label) = dimension switch
                {
                    "manufacturer" => (match.Request.Manufacturer, match.Request.Manufacturer),
                    "factory" => (match.Request.Factory, match.Request.Factory),
                    "product" => (match.Request.Product, match.Request.ProductName + " · " + match.Request.Product),
                    "status" => (match.Request.Status, match.Request.Status),
                    _ => (match.Request.Customer, match.Request.Customer)
                };
                AddOption(options, key, label);
            }
        }
        return options.Select(p => new FilterOption(p.Key, p.Value.Label, p.Value.Count)).OrderBy(o => o.Label, Keys).ThenBy(o => o.Value, Keys).ToArray();
    }

    private static void AddOption(Dictionary<string, (string Label, int Count)> options, string key, string label)
    {
        if (options.TryGetValue(key, out var previous)) options[key] = (previous.Label, previous.Count + 1);
        else options[key] = (label, 1);
    }

    private static StatusRow ToStatus(string status, AggregateCounts c) => new()
    {
        Key = status, Label = status, Requests = c.Requests, FulfilledRequests = c.FulfilledRequests,
        Units = c.Units, FulfilledUnits = c.FulfilledUnits, Lines = c.Lines, FulfilledLines = c.FulfilledLines
    };

    private static decimal MetricDemand(AggregateCounts c, string metric) => metric switch { "units" => c.Units, "lines" => c.Lines, _ => c.Requests };
    private static decimal MetricUnfulfilled(AggregateCounts c, string metric) => metric switch { "units" => c.UnfulfilledUnits, "lines" => c.UnfulfilledLines, _ => c.UnfulfilledRequests };
    private static decimal MetricFulfilment(AggregateCounts c, string metric) => metric switch { "units" => c.UnitFulfilment, "lines" => c.LineFulfilment, _ => c.RequestFulfilment };
    private static DateOnly? BasisDate(RequestFact r, string basis) => basis == "despatch" ? r.DespatchDate : r.Date;
    private static DateOnly BucketStart(DateOnly date, string bucket) => bucket switch
    {
        "month" => new DateOnly(date.Year, date.Month, 1),
        "week" => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)),
        _ => date
    };
    private static DateOnly NextBucket(DateOnly date, string bucket) => bucket switch
    {
        "month" => date.AddMonths(1), "week" => date.AddDays(7), _ => date.AddDays(1)
    };

    private static Dictionary<string, CsvRow> UniqueRows(CsvTable table, string keyColumn)
    {
        var result = new Dictionary<string, CsvRow>(Keys);
        foreach (var row in table.Rows)
            if (!result.TryAdd(row.Required(keyColumn, table.Name), row))
                throw new InvalidDataException($"{table.Name}, row {row.Number}: {keyColumn} must be unique within the snapshot.");
        return result;
    }

    private static DateOnly ParseDataDate(string value, string file, int row, string column)
    {
        string[] formats = ["d/M/yyyy", "dd/MM/yyyy", "d/M/yyyy H:mm:ss", "dd/MM/yyyy HH:mm:ss",
            "d/M/yyyy H:mm", "dd/MM/yyyy HH:mm", "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF"];
        if (DateTime.TryParseExact(value, formats, Uk, DateTimeStyles.AllowWhiteSpaces, out DateTime parsed))
            return DateOnly.FromDateTime(parsed);
        throw new InvalidDataException($"{file}, row {row}: {column} is not a valid UK date (dd/MM/yyyy, with optional time) or ISO date.");
    }

    private static bool IsFulfilled(string status) => status.Equals("Satisfied", StringComparison.OrdinalIgnoreCase) || status.Equals("Satified", StringComparison.OrdinalIgnoreCase);
    private static string Normalise(string key) => key.Trim().ToUpperInvariant();
    private static string Nonblank(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static void AddDiagnostic(List<DataDiagnostic> diagnostics, string severity, string code, string message, int count)
    {
        if (count > 0) diagnostics.Add(new(severity, code, message, count));
    }

    private static Snapshot EmptySnapshot() => new([], new Metadata
    {
        Diagnostics = Array.AsReadOnly(new[] { new DataDiagnostic("info", "no-data", "No dataset has been imported. An administrator can import the seven CSV table snapshots.") })
    });

    private sealed record ProductDefinition(string Code, string BaseProduct, string Description, string Manufacturer,
        string Factory, string FactoryLocation, bool ManufacturerMatched, bool FactoryMatched);

    private sealed class RequestBuilder(string key, DateOnly date, DateOnly? despatchDate, string product,
        string productName, string baseProduct, string manufacturer, string factory, string customer,
        string status, bool fulfilled, string factoryLocation)
    {
        public string BaseProduct { get; } = baseProduct;
        public string Product { get; } = product;
        public List<PartFact> Parts { get; } = [];
        public RequestFact ToFact() => new(key, date, despatchDate, Product, productName, BaseProduct,
            manufacturer, factory, customer, status, fulfilled, factoryLocation, Parts.ToArray());
    }

    private sealed record MatchedRequest(RequestFact Request, PartFact[] Parts);

    private sealed class PreparedFilter
    {
        public DateOnly? From { get; }
        public DateOnly? To { get; }
        public string DateBasis { get; }
        public HashSet<string> Manufacturers { get; }
        public HashSet<string> Factories { get; }
        public HashSet<string> Products { get; }
        public HashSet<string> BaseParts { get; }
        public HashSet<string> Statuses { get; }
        public HashSet<string> Customers { get; }
        public string Outcome { get; }
        public string Search { get; }
        public string GroupBy { get; }
        public string Metric { get; }
        public string Sort { get; }
        public string Direction { get; }
        public string TrendBucket { get; }
        public int Page { get; }
        public int PageSize { get; }

        public PreparedFilter(ExploreRequest request)
        {
            From = ParseFilterDate(request.From, "From"); To = ParseFilterDate(request.To, "To");
            if (From.HasValue && To.HasValue && From > To) throw new ArgumentException("From must be on or before To.");
            DateBasis = Choice(request.DateBasis, "DateBasis", "request", "despatch");
            Outcome = Choice(request.Outcome, "Outcome", "all", "fulfilled", "unfulfilled");
            GroupBy = Choice(request.GroupBy, "GroupBy", "factory", "manufacturer", "product", "basepart");
            Metric = Choice(request.Metric, "Metric", "requests", "units", "lines");
            Sort = Choice(request.Sort, "Sort", "demand", "unfulfilled", "fulfilment", "name");
            Direction = Choice(request.Direction, "Direction", "desc", "asc");
            TrendBucket = Choice(request.TrendBucket, "TrendBucket", "week", "day", "month");
            Manufacturers = Set(request.Manufacturers); Factories = Set(request.Factories);
            Products = Set(request.Products); BaseParts = Set(request.BaseParts);
            Statuses = Set(request.Statuses); Customers = Set(request.Customers);
            Search = request.Search?.Trim() ?? "";
            if (Search.Length > 500) throw new ArgumentException("Search must be 500 characters or fewer.");
            Page = Math.Max(1, request.Page);
            PageSize = Math.Clamp(request.PageSize, 1, 100);
        }

        private static HashSet<string> Set(string[]? values) => new((values ?? []).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()), Keys);
        private static string Choice(string? value, string field, params string[] choices)
        {
            string selected = string.IsNullOrWhiteSpace(value) ? choices[0] : value.Trim().ToLowerInvariant();
            if (!choices.Contains(selected)) throw new ArgumentException($"{field} must be one of: {string.Join(", ", choices)}.");
            return selected;
        }
        private static DateOnly? ParseFilterDate(string? value, string field)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
            throw new ArgumentException($"{field} must be a date in yyyy-MM-dd format.");
        }
    }
}
