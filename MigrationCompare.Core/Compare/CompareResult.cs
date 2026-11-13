using MigrationCompare.Core.Models;

namespace MigrationCompare.Core.Compare;

/// <summary>Hasil komparasi dua sumber.</summary>
public sealed class CompareResult
{
    private int? _totalA;
    private int? _totalB;
    private int? _totalMatched;
    private int? _totalMissingInB;
    private int? _totalMissingInA;
    private int? _totalDiffValues;

    public List<CompareResultRow> Rows { get; set; } = new();
    public List<string> CompareColumns { get; set; } = new();

    /// <summary>Menunjukkan apakah hasil baris di memori terpotong oleh batas MaxStoredRows.</summary>
    public bool IsTruncated { get; set; }

    public int TotalA
    {
        get => _totalA ?? Rows.Count(r => r.Status != RowStatus.MissingInA);
        set => _totalA = value;
    }

    public int TotalB
    {
        get => _totalB ?? Rows.Count(r => r.Status != RowStatus.MissingInB);
        set => _totalB = value;
    }

    public int TotalMatched
    {
        get => _totalMatched ?? Rows.Count(r => r.Status == RowStatus.Match);
        set => _totalMatched = value;
    }

    public int TotalMissingInB
    {
        get => _totalMissingInB ?? Rows.Count(r => r.Status == RowStatus.MissingInB);
        set => _totalMissingInB = value;
    }

    public int TotalMissingInA
    {
        get => _totalMissingInA ?? Rows.Count(r => r.Status == RowStatus.MissingInA);
        set => _totalMissingInA = value;
    }

    public int TotalDiffValues
    {
        get => _totalDiffValues ?? Rows.Count(r => r.Status == RowStatus.Match && r.DifferingColumns.Count > 0);
        set => _totalDiffValues = value;
    }
}
