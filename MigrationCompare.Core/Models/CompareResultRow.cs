namespace MigrationCompare.Core.Models;

public enum RowStatus { Match, MissingInA, MissingInB }

/// <summary>Hasil perbandingan satu baris.</summary>
public sealed class CompareResultRow
{
    public string Key { get; set; } = "";
    public RowStatus Status { get; set; }
    public DataRow? RowA { get; set; }
    public DataRow? RowB { get; set; }

    /// <summary>Kolom yang nilainya beda (hanya untuk Match).</summary>
    public List<string> DifferingColumns { get; set; } = new();
}
