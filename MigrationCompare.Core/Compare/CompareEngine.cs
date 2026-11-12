using MigrationCompare.Core.Models;
using MigrationCompare.Core.Sources;

namespace MigrationCompare.Core.Compare;

/// <summary>Engine komparasi dua sumber berbasis key join + banding value.</summary>
public sealed class CompareEngine
{
    private readonly DataSourceDefinition _a;
    private readonly DataSourceDefinition _b;

    /// <summary>Kolom kunci dari sumber B (jika tidak diisi, pakai yang dari A).</summary>
    private readonly IReadOnlyList<string> _bKeys;

    /// <summary>Toleransi perbandingan numerik (0 = harus sama persis).</summary>
    private readonly decimal _numericTolerance;

    public CompareEngine(DataSourceDefinition a, DataSourceDefinition b, decimal numericTolerance = 0m)
    {
        if (a.KeyColumns.Count == 0) throw new ArgumentException("Sumber A harus punya minimal satu kolom kunci.", nameof(a));
        _a = a;
        _b = b;
        _bKeys = b.KeyColumns.Count > 0 ? b.KeyColumns : a.KeyColumns;
        _numericTolerance = numericTolerance;
    }

    /// <summary>Menjalankan komparasi dan mengembalikan hasil lengkap.</summary>
    public CompareResult Compare()
    {
        using var sourceA = DataSourceFactory.Create(_a);
        using var sourceB = DataSourceFactory.Create(_b);

        var colsA = sourceA.Open();
        var colsB = sourceB.Open();

        // Tentukan kolom yang dibandingkan (default = semua non-key pada A).
        var compareCols = ResolveCompareColumns(colsA, colsB);
        var result = new CompareResult { CompareColumns = compareCols.ToList() };

        // 1) Baca semua baris A ke dalam dictionary key -> row.
        var mapA = new Dictionary<string, DataRow>(StringComparer.OrdinalIgnoreCase);
        var keyListA = new List<string>();
        while (sourceA.ReadRow() is { } rowA)
        {
            var key = BuildKey(rowA, _a.KeyColumns);
            mapA[key] = rowA;
            keyListA.Add(key);
        }

        // 2) Baca B satu per satu, cocokkan dengan A.
        var matchedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (sourceB.ReadRow() is { } rowB)
        {
            var key = BuildKey(rowB, _bKeys);
            // Duplikat key di B: hanya proses yang pertama untuk kesederhanaan v1.
            if (!matchedKeys.Add(key)) continue;

            var cr = new CompareResultRow { Key = key, RowB = rowB };
            if (mapA.TryGetValue(key, out var rowA))
            {
                cr.Status = RowStatus.Match;
                cr.RowA = rowA;
                foreach (var col in compareCols)
                {
                    var va = Normalize(rowA.Get(col));
                    var vb = Normalize(rowB.Get(col));
                    if (!ValuesEqual(va, vb)) cr.DifferingColumns.Add(col);
                }
            }
            else
            {
                cr.Status = RowStatus.MissingInA;
            }
            result.Rows.Add(cr);
        }

        // 3) Baris A yang tidak ada di B.
        foreach (var key in keyListA)
        {
            if (matchedKeys.Contains(key)) continue;
            result.Rows.Add(new CompareResultRow
            {
                Key = key,
                Status = RowStatus.MissingInB,
                RowA = mapA[key]
            });
        }

        return result;
    }

    private static string BuildKey(DataRow row, IReadOnlyList<string> keys)
    {
        var parts = new string[keys.Count];
        for (int i = 0; i < keys.Count; i++)
        {
            parts[i] = row.Get(keys[i]) ?? "";
        }
        return string.Join('\u001F', parts); // pemisah yang jarang muncul di data
    }

    private IReadOnlyList<string> ResolveCompareColumns(IReadOnlyList<string> colsA, IReadOnlyList<string> colsB)
    {
        // Jika user mendefinisikan CompareColumns, pakai itu.
        if (_a.CompareColumns.Count > 0) return _a.CompareColumns;

        // Default: semua kolom A kecuali key; cocokkan dengan posisi yang sama di B.
        var resultL = new List<string>();
        for (int i = 0; i < colsA.Count; i++)
        {
            var colA = colsA[i];
            if (_a.KeyColumns.Any(k => string.Equals(k, colA, StringComparison.OrdinalIgnoreCase))) continue;
            // Pastikan posisi ada di B.
            if (i < colsB.Count) resultL.Add(colA);
        }
        return resultL;
    }

    private static string? Normalize(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private bool ValuesEqual(string? a, string? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;

        // Coba bandingkan sebagai angka dengan toleransi.
        if (decimal.TryParse(a, out var na) && decimal.TryParse(b, out var nb))
        {
            return Math.Abs(na - nb) <= _numericTolerance;
        }
        return false;
    }
}
