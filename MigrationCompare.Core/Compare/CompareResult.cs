using MigrationCompare.Core.Models;
using MigrationCompare.Core.Sources;

namespace MigrationCompare.Core.Compare;

/// <summary>Hasil komparasi dua sumber.</summary>
public sealed class CompareResult
{
    public List<CompareResultRow> Rows { get; set; } = new();
    public List<string> CompareColumns { get; set; } = new();

    public int TotalA => Rows.Count(r => r.Status != RowStatus.MissingInA);
    public int TotalB => Rows.Count(r => r.Status != RowStatus.MissingInB);
    public int TotalMatched => Rows.Count(r => r.Status == RowStatus.Match);
    public int TotalMissingInB => Rows.Count(r => r.Status == RowStatus.MissingInB);
    public int TotalMissingInA => Rows.Count(r => r.Status == RowStatus.MissingInA);
    public int TotalDiffValues => Rows.Count(r => r.Status == RowStatus.Match && r.DifferingColumns.Count > 0);
}
