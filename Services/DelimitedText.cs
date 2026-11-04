using System.Security.Cryptography;
using System.Text;
using ClickSpares.SparesExplorer.Models;

namespace ClickSpares.SparesExplorer.Services;

/// <summary>
/// Parses quoted delimited files, and repairs only the three known legacy export layouts
/// whose descriptive columns contain unquoted commas. It never guesses across other schemas.
/// </summary>
internal static class DelimitedText
{
    private const long MaximumFileBytes = 128L * 1024 * 1024;

    // Shared with the account usage log, which already writes properly quoted CSV.
    public static IReadOnlyList<IReadOnlyList<string>> Parse(string text) =>
        Parse(text, DetectDelimiter(text), "CSV").Select(r => (IReadOnlyList<string>)r.Fields).ToArray();

    public static CsvTable Read(string path, string tableName)
    {
        var file = new FileInfo(path);
        if (file.Length > MaximumFileBytes)
            throw new InvalidDataException($"{file.Name} exceeds the 128 MB import limit.");

        byte[] bytes = File.ReadAllBytes(path);
        string text;
        bool legacyEncoding = false;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
            text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
            text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        else
        {
            try { text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\ufeff'); }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                text = Encoding.GetEncoding(1252).GetString(bytes);
                legacyEncoding = true;
            }
        }

        if (text.Contains('\0'))
            throw new InvalidDataException($"{file.Name} contains null characters; save it as UTF-8 CSV.");

        char delimiter = DetectDelimiter(text);
        var records = Parse(text, delimiter, file.Name);
        if (records.Count == 0)
            throw new InvalidDataException($"{file.Name} has no header row.");
        string[] headers = records[0].Fields.Select(h => h.Trim().TrimStart('\ufeff')).ToArray();
        if (headers.Any(string.IsNullOrWhiteSpace) || headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Length)
            throw new InvalidDataException($"{file.Name} has an empty or duplicate column heading.");

        var rows = new List<CsvRow>(records.Count - 1);
        int repairedRows = 0;
        foreach (var record in records.Skip(1))
        {
            string[] fields = record.Fields;
            if (delimiter == ',' && fields.Length > headers.Length && TryRepair(tableName, headers, fields, out var repaired))
            {
                fields = repaired;
                repairedRows++;
            }
            if (fields.Length != headers.Length)
                throw new InvalidDataException($"{file.Name}, row {record.RowNumber}: expected {headers.Length} columns, found {fields.Length}. Quote fields containing commas.");
            rows.Add(new CsvRow(record.RowNumber, headers.Zip(fields).ToDictionary(x => x.First, x => x.Second.Trim(), StringComparer.OrdinalIgnoreCase)));
        }

        var diagnostics = new List<DataDiagnostic>();
        if (repairedRows > 0)
            diagnostics.Add(new("warning", "csv-commas-repaired", $"Rejoined unquoted commas in {repairedRows:N0} descriptive fields in {file.Name}.", repairedRows));
        if (legacyEncoding)
            diagnostics.Add(new("info", "legacy-encoding", $"Read {file.Name} using Windows-1252 encoding.", 1));
        return new CsvTable(file.Name, headers, rows, repairedRows,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), diagnostics);
    }

    private static bool TryRepair(string name, string[] headers, string[] fields, out string[] result)
    {
        result = fields;
        bool Is(params string[] expected) => headers.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase);
        if (name == "baseparts" && Is("BasePart", "Description"))
            result = [fields[0], string.Join(',', fields.Skip(1))];
        else if (name == "manufacturers" && Is("ManufacturerCode", "Manufacturer", "ChargeTo"))
            result = [fields[0], string.Join(',', fields.Skip(1).Take(fields.Length - 2)), fields[^1]];
        else if (name == "products" && Is("Catalogue", "Product", "BaseProduct", "Description", "Manufacturer", "Factory"))
            result = [fields[0], fields[1], fields[2], string.Join(',', fields.Skip(3).Take(fields.Length - 5)), fields[^2], fields[^1]];
        else return false;
        return true;
    }

    private static char DetectDelimiter(string text)
    {
        int commas = 0, tabs = 0, semicolons = 0;
        bool quoted = false;
        foreach (char c in text)
        {
            if (c == '"') quoted = !quoted;
            if (!quoted && (c == '\r' || c == '\n')) break;
            if (quoted) continue;
            if (c == ',') commas++;
            else if (c == '\t') tabs++;
            else if (c == ';') semicolons++;
        }
        return tabs > commas && tabs > semicolons ? '\t' : semicolons > commas ? ';' : ',';
    }

    private static List<RawRecord> Parse(string text, char delimiter, string fileName)
    {
        var records = new List<RawRecord>();
        var fields = new List<string>();
        var field = new StringBuilder();
        bool quoted = false, afterQuote = false, fieldStarted = false;
        int physicalLine = 1, recordLine = 1;

        void EndField()
        {
            fields.Add(field.ToString());
            field.Clear();
            afterQuote = false;
            fieldStarted = false;
        }

        void EndRecord()
        {
            EndField();
            if (fields.Any(v => !string.IsNullOrWhiteSpace(v)))
                records.Add(new RawRecord(recordLine, fields.ToArray()));
            fields.Clear();
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else { quoted = false; afterQuote = true; }
                }
                else
                {
                    field.Append(c);
                    if (c == '\n') physicalLine++;
                    else if (c == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')) physicalLine++;
                }
                continue;
            }
            if (c == delimiter) { EndField(); continue; }
            if (c is '\r' or '\n')
            {
                EndRecord();
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                physicalLine++;
                recordLine = physicalLine;
                continue;
            }
            if (afterQuote)
            {
                if (c is ' ' or '\t') continue;
                throw new InvalidDataException($"{fileName}, row {recordLine}: unexpected text after a closing quote.");
            }
            if (c == '"' && !fieldStarted)
            {
                quoted = true;
                fieldStarted = true;
                continue;
            }
            field.Append(c);
            fieldStarted = true;
        }
        if (quoted)
            throw new InvalidDataException($"{fileName}, row {recordLine}: an opening quote has no closing quote.");
        if (field.Length > 0 || fields.Count > 0 || fieldStarted || afterQuote) EndRecord();
        return records;
    }

    private sealed record RawRecord(int RowNumber, string[] Fields);
}

internal sealed record CsvTable(string Name, string[] Headers, List<CsvRow> Rows, int RepairedRows, string Hash, IReadOnlyList<DataDiagnostic> Diagnostics)
{
    public void Require(params string[] columns)
    {
        string[] missing = columns.Where(c => !Headers.Contains(c, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException($"{Name} is missing required column(s): {string.Join(", ", missing)}.");
    }
}

internal sealed record CsvRow(int Number, Dictionary<string, string> Values)
{
    public string this[string column] => Values.GetValueOrDefault(column, "");

    public string Required(string column, string fileName)
    {
        string value = this[column];
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"{fileName}, row {Number}: {column} is required.");
        return value;
    }
}
