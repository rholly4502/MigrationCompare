using System.Globalization;
using Microsoft.Data.SqlClient;
using MigrationCompare.Core.Models;

namespace MigrationCompare.Core.Sources;

/// <summary>Hasil analisis pembagian segmen untuk satu sumber SQL.</summary>
public sealed record SqlSegmentAnalysis(
    string KeyColumn,
    long TotalRows,
    int Segments,
    IReadOnlyList<string> UpperBounds);

/// <summary>Membagi satu query SQL besar menjadi beberapa segmen range berdasarkan kolom kunci,
/// mengikuti pola parallel-load `ranges` ala AWS DMS. Tool menghitung COUNT dan boundary sendiri,
/// pengguna cukup menentukan jumlah segmen.</summary>
public static class SqlSegmenter
{
    /// <summary>Menjalankan query count + boundary (NTILE) untuk menurunkan segmen.
    /// Membutuhkan kolom kunci tunggal (numerik/string/date).</summary>
    public static async Task<SqlSegmentAnalysis> AnalyzeAsync(
        DataSourceDefinition def,
        string keyColumn,
        int segments,
        CancellationToken cancellationToken = default)
    {
        if (segments < 2) throw new ArgumentOutOfRangeException(nameof(segments), "Segmen minimal 2.");
        if (string.IsNullOrWhiteSpace(keyColumn))
            throw new ArgumentException("Kolom kunci wajib untuk auto-split.", nameof(keyColumn));

        var connStr = SqlSource.ResolveConnectionString(def);
        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(cancellationToken);

        long total;
        await using (var cmd = new SqlCommand($"SELECT COUNT_BIG(*) FROM ({def.Query}) AS _t", conn)
        {
            CommandTimeout = 0
        })
        {
            total = (long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
        }

        // Boundary per segmen: nilai key MAKSIMAL dari tiap segmen (NTILE).
        // Segmen i mencakup (bounds[i-1], bounds[i]]; segmen pertama <= bounds[0]; terakhir > bounds[N-2].
        var bounds = new List<string>();
        await using (var cmd = new SqlCommand(
            $"SELECT seg, MAX(_k) FROM (SELECT {QuoteIdent(keyColumn)} AS _k, " +
            $"NTILE(@seg) OVER (ORDER BY {QuoteIdent(keyColumn)}) AS seg FROM ({def.Query}) AS _t) AS _x " +
            "GROUP BY seg ORDER BY seg",
            conn)
        {
            CommandTimeout = 0
        })
        {
            cmd.Parameters.AddWithValue("@seg", segments);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                bounds.Add(reader.IsDBNull(1) ? "" : reader.GetValue(1)?.ToString() ?? "");
            }
        }

        return new SqlSegmentAnalysis(keyColumn, total, bounds.Count, bounds);
    }

    /// <summary>Mendeteksi apakah kolom kunci bertipe numerik (via SchemaOnly) agar literal boundary bisa tanpa kutip.</summary>
    public static async Task<bool> DetectNumericKeyAsync(
        DataSourceDefinition def,
        string keyColumn,
        CancellationToken cancellationToken = default)
    {
        var connStr = SqlSource.ResolveConnectionString(def);
        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new SqlCommand($"SELECT * FROM ({def.Query}) AS _t WHERE 1 = 0", conn)
        {
            CommandTimeout = 0
        };
        await using var reader = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SchemaOnly, cancellationToken);
        for (int i = 0; i < reader.FieldCount; i++)
        {
            if (reader.GetName(i).Equals(keyColumn, StringComparison.OrdinalIgnoreCase))
            {
                return IsNumericType(reader.GetFieldType(i));
            }
        }
        return false;
    }

    /// <summary>Mengubah definisi sumber menjadi N definisi segmen ber-query dengan WHERE range.</summary>
    public static IReadOnlyList<DataSourceDefinition> BuildSegmentDefinitions(
        DataSourceDefinition source,
        SqlSegmentAnalysis analysis,
        bool numericKey)
    {
        var result = new List<DataSourceDefinition>();
        var key = QuoteIdent(analysis.KeyColumn);
        var baseQuery = source.Query;

        for (int i = 0; i < analysis.Segments; i++)
        {
            var lower = i == 0 ? (string?)null : analysis.UpperBounds[i - 1];
            var upper = i == analysis.Segments - 1 ? (string?)null : analysis.UpperBounds[i];

            string where;
            if (lower is null)
            {
                where = $"{key} <= {Literal(upper!, numericKey)}";
            }
            else if (upper is null)
            {
                where = $"{key} > {Literal(lower, numericKey)}";
            }
            else
            {
                where = $"{key} > {Literal(lower, numericKey)} AND {key} <= {Literal(upper, numericKey)}";
            }

            result.Add(new DataSourceDefinition
            {
                Name = $"{source.Name}[{i + 1}/{analysis.Segments}]",
                Kind = source.Kind,
                Connection = source.Connection,
                Server = source.Server,
                Database = source.Database,
                Username = source.Username,
                Password = source.Password,
                UseWindowsAuth = source.UseWindowsAuth,
                Query = $"SELECT * FROM ({baseQuery}) AS _s WHERE {where}",
                KeyColumns = source.KeyColumns.ToList(),
                CompareColumns = source.CompareColumns.ToList()
            });
        }

        return result;
    }

    /// <summary>Menentukan apakah kolom kunci bernilai numerik (agar literal tidak dikutip).</summary>
    public static bool IsNumericType(Type? fieldType) => fieldType is not null && (
        fieldType == typeof(byte) || fieldType == typeof(sbyte) ||
        fieldType == typeof(short) || fieldType == typeof(ushort) ||
        fieldType == typeof(int) || fieldType == typeof(uint) ||
        fieldType == typeof(long) || fieldType == typeof(ulong) ||
        fieldType == typeof(decimal) || fieldType == typeof(double) ||
        fieldType == typeof(float));

    private static string Literal(string value, bool numeric)
    {
        if (numeric) return value;
        return "'" + value.Replace("'", "''") + "'";
    }

    private static string QuoteIdent(string name) => name.Replace("]", "]]") switch
    {
        var n => n.Contains('[') ? n : "[" + n + "]"
    };
}
