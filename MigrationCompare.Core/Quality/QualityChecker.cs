using MigrationCompare.Core.Models;
using MigrationCompare.Core.Sources;

namespace MigrationCompare.Core.Quality;

/// <summary>Hasil pemeriksaan kualitas satu sumber.</summary>
public sealed class QualityReport
{
    public string SourceName { get; set; } = "";
    public long TotalRows { get; set; }
    public long NullCells { get; set; }
    public long DuplicateKeys { get; set; }
    public List<string> Columns { get; set; } = new();
    public Dictionary<string, long> NullPerColumn { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, long> EmptyPerColumn { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, long> DuplicateCountPerKey { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Memeriksa kualitas data dari satu sumber: null, kosong, duplikat key.</summary>
public sealed class QualityChecker
{
    private const int MaxTrackedDuplicateKeys = 50_000;
    private readonly DataSourceDefinition _def;

    public QualityChecker(DataSourceDefinition def) => _def = def;

    public QualityReport Check() => Check(null, default);

    public QualityReport Check(IProgress<long>? progress, CancellationToken cancellationToken = default)
    {
        using var source = DataSourceFactory.Create(_def);
        var columns = source.Open();

        var report = new QualityReport
        {
            SourceName = _def.Name,
            Columns = columns.ToList()
        };
        foreach (var c in columns)
        {
            report.NullPerColumn[c] = 0;
            report.EmptyPerColumn[c] = 0;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (source.ReadRow() is { } row)
        {
            cancellationToken.ThrowIfCancellationRequested();
            report.TotalRows++;

            // Null & kosong per kolom.
            foreach (var c in columns)
            {
                var v = row.Get(c);
                if (v is null) report.NullPerColumn[c]++;
                else if (string.IsNullOrWhiteSpace(v)) report.EmptyPerColumn[c]++;
            }

            // Duplikat key (tetap hitung total duplikat, batasi detail key agar hemat RAM).
            if (_def.KeyColumns.Count > 0)
            {
                var key = BuildKey(row);
                if (!seen.Add(key))
                {
                    report.DuplicateKeys++;
                    if (report.DuplicateCountPerKey.TryGetValue(key, out var n))
                    {
                        report.DuplicateCountPerKey[key] = n + 1;
                    }
                    else if (report.DuplicateCountPerKey.Count < MaxTrackedDuplicateKeys)
                    {
                        report.DuplicateCountPerKey[key] = 1;
                    }
                }
            }

            if ((report.TotalRows & 0x3FFF) == 0) progress?.Report(report.TotalRows);
        }

        report.NullCells = report.NullPerColumn.Values.Sum();
        progress?.Report(report.TotalRows);
        return report;
    }

    private string BuildKey(DataRow row)
    {
        if (_def.KeyColumns.Count == 1)
            return row.Get(_def.KeyColumns[0]) ?? "";

        var parts = new string[_def.KeyColumns.Count];
        for (int i = 0; i < _def.KeyColumns.Count; i++)
        {
            parts[i] = row.Get(_def.KeyColumns[i]) ?? "";
        }
        return string.Join('\u001F', parts);
    }
}
