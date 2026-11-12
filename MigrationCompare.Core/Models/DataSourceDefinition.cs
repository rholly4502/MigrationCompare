namespace MigrationCompare.Core.Models;

/// <summary>Mendeskripsikan satu sumber data (SQL/CSV) yang akan dibandingkan.</summary>
public sealed class DataSourceDefinition
{
    public string Name { get; set; } = "";

    /// <summary>"sql" | "csv"</summary>
    public string Kind { get; set; } = "sql";

    /// <summary>Connection string (untuk SQL) atau path file (untuk CSV).</summary>
    public string Connection { get; set; } = "";

    /// <summary>Query SELECT (untuk SQL). Diabaikan untuk CSV.</summary>
    public string Query { get; set; } = "";

    /// <summary>Nama kolom yang menjadi kunci join. Wajib minimal satu.</summary>
    public List<string> KeyColumns { get; set; } = new();

    /// <summary>Nama kolom yang ikut dibandingkan nilainya.
    /// Kosong = semua kolom non-key.</summary>
    public List<string> CompareColumns { get; set; } = new();
}
