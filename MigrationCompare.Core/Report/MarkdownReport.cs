using System.Globalization;
using System.Text;
using MigrationCompare.Core.Compare;
using MigrationCompare.Core.Models;
using MigrationCompare.Core.Quality;

namespace MigrationCompare.Core.Report;

/// <summary>Generator laporan hasil komparasi & kualitas dalam format Markdown.</summary>
public static class MarkdownReport
{
    public static string Compare(CompareResult result, string nameA, string nameB, int topDetail = 50)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Laporan Komparasi Data");
        sb.AppendLine();
        sb.AppendLine($"- **Sumber A:** {nameA}");
        sb.AppendLine($"- **Sumber B:** {nameB}");
        sb.AppendLine($"- **Waktu:** {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Kolom dibandingkan:** {string.Join(", ", result.CompareColumns)}");
        sb.AppendLine();
        sb.AppendLine("## Ringkasan");
        sb.AppendLine();
        sb.AppendLine("| Metrik | Jumlah |");
        sb.AppendLine("|--------|--------|");
        sb.AppendLine($"| Total baris A | {result.TotalA} |");
        sb.AppendLine($"| Total baris B | {result.TotalB} |");
        sb.AppendLine($"| Cocok (matched) | {result.TotalMatched} |");
        sb.AppendLine($"| Ada di A, tidak di B | {result.TotalMissingInB} |");
        sb.AppendLine($"| Ada di B, tidak di A | {result.TotalMissingInA} |");
        sb.AppendLine($"| Cocok tapi ada nilai beda | {result.TotalDiffValues} |");
        sb.AppendLine();

        // Detail hanya untuk baris yang nilai-nya beda.
        var diffs = result.Rows
            .Where(r => r.Status == RowStatus.Match && r.DifferingColumns.Count > 0)
            .OrderByDescending(r => r.DifferingColumns.Count)
            .Take(topDetail)
            .ToList();

        if (diffs.Count > 0)
        {
            sb.AppendLine($"## Detail perbedaan nilai (top {diffs.Count})");
            sb.AppendLine();
            foreach (var d in diffs)
            {
                sb.AppendLine($"- **Key:** `{d.Key}`");
                foreach (var col in d.DifferingColumns)
                {
                    sb.AppendLine($"  - `{col}`: A=`{d.RowA?.Get(col)}` | B=`{d.RowB?.Get(col)}`");
                }
            }
            sb.AppendLine();
        }

        var missing = result.Rows.Where(r => r.Status != RowStatus.Match).Take(topDetail).ToList();
        if (missing.Count > 0)
        {
            sb.AppendLine($"## Baris tidak cocok (top {missing.Count})");
            sb.AppendLine();
            foreach (var m in missing)
            {
                var what = m.Status == RowStatus.MissingInB ? "hanya di A" : "hanya di B";
                sb.AppendLine($"- **Key:** `{m.Key}` — {what}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string Quality(QualityReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Laporan Kualitas Data — {report.SourceName}");
        sb.AppendLine();
        sb.AppendLine($"- **Total baris:** {report.TotalRows}");
        sb.AppendLine($"- **Total sel NULL:** {report.NullCells}");
        sb.AppendLine($"- **Key duplikat:** {report.DuplicateKeys}");
        sb.AppendLine();

        if (report.NullPerColumn.Any(c => c.Value > 0) || report.EmptyPerColumn.Any(c => c.Value > 0))
        {
            sb.AppendLine("## Kolom dengan NULL / kosong");
            sb.AppendLine();
            sb.AppendLine("| Kolom | NULL | Kosong |");
            sb.AppendLine("|--------|------|--------|");
            foreach (var c in report.Columns)
            {
                var n = report.NullPerColumn.TryGetValue(c, out var nv) ? nv : 0;
                var e = report.EmptyPerColumn.TryGetValue(c, out var ev) ? ev : 0;
                if (n > 0 || e > 0) sb.AppendLine($"| {c} | {n} | {e} |");
            }
            sb.AppendLine();
        }

        if (report.DuplicateCountPerKey.Count > 0)
        {
            sb.AppendLine("## Key duplikat (sering muncul)");
            sb.AppendLine();
            sb.AppendLine("| Key | Jumlah duplikat tambahan |");
            sb.AppendLine("|-----|--------------------------|");
            foreach (var kv in report.DuplicateCountPerKey.OrderByDescending(k => k.Value).Take(20))
            {
                sb.AppendLine($"| `{kv.Key}` | {kv.Value} |");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
