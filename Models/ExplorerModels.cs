namespace ClickSpares.SparesExplorer.Models;

/// <summary>Selections within a dimension are ORed; different dimensions are ANDed.</summary>
public sealed record ExploreRequest
{
    public string? From { get; init; }
    public string? To { get; init; }
    public string DateBasis { get; init; } = "request";
    public string[] Manufacturers { get; init; } = [];
    public string[] Factories { get; init; } = [];
    public string[] Products { get; init; } = [];
    public string[] BaseParts { get; init; } = [];
    public string[] Statuses { get; init; } = [];
    public string[] Customers { get; init; } = [];
    public string Outcome { get; init; } = "all";
    public string Search { get; init; } = "";
    public string GroupBy { get; init; } = "factory";
    public string Metric { get; init; } = "requests";
    public string Sort { get; init; } = "demand";
    public string Direction { get; init; } = "desc";
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
    public string TrendBucket { get; init; } = "week";
}

/// <summary>Rates are percentages (0–100). Zero-denominator rates are zero.</summary>
public record AggregateCounts
{
    public int Requests { get; init; }
    public int FulfilledRequests { get; init; }
    public decimal Units { get; init; }
    public decimal FulfilledUnits { get; init; }
    public int Lines { get; init; }
    public int FulfilledLines { get; init; }
    public int UnfulfilledRequests => Requests - FulfilledRequests;
    public decimal UnfulfilledUnits => Units - FulfilledUnits;
    public int UnfulfilledLines => Lines - FulfilledLines;
    public decimal RequestFulfilment => Percentage(FulfilledRequests, Requests);
    public decimal UnitFulfilment => Percentage(FulfilledUnits, Units);
    public decimal LineFulfilment => Percentage(FulfilledLines, Lines);

    private static decimal Percentage(decimal numerator, decimal denominator) =>
        denominator == 0 ? 0 : numerator / denominator * 100;
}

public sealed record ExplorerSummary : AggregateCounts
{
    public int Manufacturers { get; init; }
    public int Factories { get; init; }
    public int Products { get; init; }
    public int BaseParts { get; init; }
    public int NoPartRequests { get; init; }
}

public sealed record GroupRow : AggregateCounts
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public decimal Demand { get; init; }
    public decimal Unfulfilled { get; init; }
    public decimal Fulfilment { get; init; }
}

public sealed record TrendPoint : AggregateCounts
{
    public string Date { get; init; } = "";
    public string Label { get; init; } = "";
    public decimal Demand { get; init; }
    public decimal Unfulfilled { get; init; }
    public decimal Fulfilment { get; init; }
}

public sealed record StatusRow : AggregateCounts
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
}

public sealed record FilterOption(string Value, string Label, int Requests);

public sealed record ExplorerFacets
{
    public IReadOnlyList<FilterOption> Manufacturers { get; init; } = [];
    public IReadOnlyList<FilterOption> Factories { get; init; } = [];
    public IReadOnlyList<FilterOption> Products { get; init; } = [];
    public IReadOnlyList<FilterOption> BaseParts { get; init; } = [];
    public IReadOnlyList<FilterOption> Statuses { get; init; } = [];
    public IReadOnlyList<FilterOption> Customers { get; init; } = [];
}

public sealed record DataDiagnostic(string Severity, string Code, string Message, int Count = 0);
public sealed record SourceFileInfo(string Name, int Rows, int RepairedRows);

public sealed record Metadata
{
    public string Version { get; init; } = "empty";
    public DateTimeOffset LoadedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool HasData { get; init; }
    public int RequestCount { get; init; }
    public int LineCount { get; init; }
    public string? RequestDateFrom { get; init; }
    public string? RequestDateTo { get; init; }
    public string? DespatchDateFrom { get; init; }
    public string? DespatchDateTo { get; init; }
    public IReadOnlyList<SourceFileInfo> SourceFiles { get; init; } = [];
    public IReadOnlyList<DataDiagnostic> Diagnostics { get; init; } = [];
}

public sealed record ExploreResponse
{
    public ExplorerSummary Summary { get; init; } = new();
    public IReadOnlyList<GroupRow> Rows { get; init; } = [];
    public IReadOnlyList<TrendPoint> Trend { get; init; } = [];
    public IReadOnlyList<StatusRow> Statuses { get; init; } = [];
    public ExplorerFacets Facets { get; init; } = new();
    public Metadata Metadata { get; init; } = new();
    public string GroupBy { get; init; } = "factory";
    public string Metric { get; init; } = "requests";
    public string DateBasis { get; init; } = "request";
    public string Sort { get; init; } = "demand";
    public string Direction { get; init; } = "desc";
    public string TrendBucket { get; init; } = "week";
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
    public int TotalRows { get; init; }
    public int TotalPages { get; init; } = 1;
}

public sealed record RequestPart(string PartId, string BasePart, string Description, decimal Quantity);

public sealed record RequestRecord
{
    public string Key { get; init; } = "";
    public string Date { get; init; } = "";
    public string? DespatchDate { get; init; }
    public string Product { get; init; } = "";
    public string ProductName { get; init; } = "";
    public string BaseProduct { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Factory { get; init; } = "";
    public string Customer { get; init; } = "";
    public string Status { get; init; } = "";
    public bool Fulfilled { get; init; }
    public decimal Units { get; init; }
    public int Lines { get; init; }
    public IReadOnlyList<RequestPart> Parts { get; init; } = [];
}

public sealed record RequestDetailsResponse
{
    public IReadOnlyList<RequestRecord> Rows { get; init; } = [];
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
    public int TotalRows { get; init; }
    public int TotalPages { get; init; } = 1;
}

/// <summary>A complete, immutable set of request facts and their part facts.</summary>
public sealed class Snapshot
{
    internal RequestFact[] Requests { get; }
    public Metadata Metadata { get; }

    internal Snapshot(RequestFact[] requests, Metadata metadata)
    {
        Requests = requests;
        Metadata = metadata;
    }
}

internal sealed record PartFact(string PartId, string BasePart, string Description, decimal Quantity);

internal sealed record RequestFact(
    string Key, DateOnly Date, DateOnly? DespatchDate, string Product, string ProductName,
    string BaseProduct, string Manufacturer, string Factory, string Customer, string Status,
    bool Fulfilled, string FactoryLocation, PartFact[] Parts);
