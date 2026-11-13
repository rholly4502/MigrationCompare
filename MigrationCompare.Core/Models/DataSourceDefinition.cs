namespace MigrationCompare.Core.Models;

/// <summary>Mendeskripsikan satu sumber data (SQL/CSV) yang akan dibandingkan.</summary>
public sealed class DataSourceDefinition
{
    public string Name { get; set; } = "";

    /// <summary>"sql" | "csv"</summary>
    public string Kind { get; set; } = "sql";

    /// <summary>Connection string (untuk SQL) atau path file (untuk CSV).</summary>
    public string Connection { get; set; } = "";

    /// <summary>Host/Server SQL (opsional jika sudah tercantum di Connection).</summary>
    public string Server { get; set; } = "";

    /// <summary>Nama Database SQL (opsional jika sudah tercantum di Connection).</summary>
    public string Database { get; set; } = "";

    /// <summary>Username SQL Authentication (jika diisi, Trusted_Connection/Windows Auth dinonaktifkan).</summary>
    public string Username { get; set; } = "";

    /// <summary>Password SQL Authentication.</summary>
    public string Password { get; set; } = "";

    /// <summary>Gunakan Windows Authentication (hanya bila Username kosong).</summary>
    public bool UseWindowsAuth { get; set; }

    /// <summary>Query SELECT (untuk SQL). Diabaikan untuk CSV.</summary>
    public string Query { get; set; } = "";

    /// <summary>Nama kolom yang menjadi kunci join. Wajib minimal satu.</summary>
    public List<string> KeyColumns { get; set; } = new();

    /// <summary>Nama kolom yang ikut dibandingkan nilainya.
    /// Kosong = semua kolom non-key.</summary>
    public List<string> CompareColumns { get; set; } = new();
}
