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
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        return value;
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
            else
            {
                var row = r.Status == RowStatus.MissingInB ? r.RowA : r.RowB;
                var firstKeyCol = row?.Values.Keys.FirstOrDefault() ?? "";
                var val = row?.Get(firstKeyCol) ?? "";
                sb.AppendLine($"{Escape(r.Key)},{status},,{Escape(val)},");
            }
        }

        return sb.ToString();
    }

    public static string Quality(QualityReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("kolom,null,kosong,duplikat_key");

        foreach (var c in report.Columns)
        {
            var n = report.NullPerColumn.TryGetValue(c, out var nv) ? nv : 0;
            var e = report.EmptyPerColumn.TryGetValue(c, out var ev) ? ev : 0;
            var d = report.DuplicateCountPerKey.TryGetValue(c, out var dv) ? dv : 0;
            sb.AppendLine($"{Escape(c)},{n},{e},{d}");
        }

        return sb.ToString();
    }
}
