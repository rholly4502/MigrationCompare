using System.Text;
using MigrationCompare.Core.Compare;
using MigrationCompare.Core.Models;
using MigrationCompare.Core.Quality;

namespace MigrationCompare.Core.Report;

/// <summary>Generator laporan CSV untuk komparasi & kualitas.</summary>
public static class CsvReport
{
    private static string Escape(string? value)
    {
        if (value is null) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        return value;
    }

    private static string SummarizeRow(DataRow? row)
    {
        if (row is null) return "";
        return string.Join("; ", row.EnumerateValues()
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"{kv.Key}={kv.Value}"));
    }

    public static string Compare(CompareResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("key,status,kolom_beda,nilai_a,nilai_b");

        foreach (var r in result.Rows)
        {
            var status = r.Status switch
            {
                RowStatus.Match => "match",
                RowStatus.MissingInB => "missing_di_B",
                RowStatus.MissingInA => "missing_di_A",
                _ => ""
            };

            if (r.Status == RowStatus.Match)
            {
                if (r.DifferingColumns.Count == 0)
                {
                    sb.AppendLine($"{Escape(r.Key)},{status},,,");
                }
                else
                {
                    foreach (var col in r.DifferingColumns)
                    {
                        sb.AppendLine($"{Escape(r.Key)},{status},{Escape(col)},{Escape(r.RowA?.Get(col))},{Escape(r.RowB?.Get(col))}");
                    }
                }
            }
            else if (r.Status == RowStatus.MissingInB)
            {
                sb.AppendLine($"{Escape(r.Key)},{status},,{Escape(SummarizeRow(r.RowA))},");
            }
            else if (r.Status == RowStatus.MissingInA)
            {
                sb.AppendLine($"{Escape(r.Key)},{status},,,{Escape(SummarizeRow(r.RowB))}");
            }
        }

        return sb.ToString();
    }

    public static string Quality(QualityReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("kolom,null,kosong");

        foreach (var c in report.Columns)
        {
            var n = report.NullPerColumn.TryGetValue(c, out var nv) ? nv : 0;
            var e = report.EmptyPerColumn.TryGetValue(c, out var ev) ? ev : 0;
            sb.AppendLine($"{Escape(c)},{n},{e}");
        }

        if (report.DuplicateCountPerKey.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("key_duplikat,jumlah_duplikat_tambahan");
            foreach (var kv in report.DuplicateCountPerKey.OrderByDescending(k => k.Value))
            {
                sb.AppendLine($"{Escape(kv.Key)},{kv.Value}");
            }
        }

        return sb.ToString();
    }
}
