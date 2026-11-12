namespace MigrationCompare.Core.Models;

/// <summary>Representasi satu baris data dari sumber mana pun (SQL/CSV).</summary>
public sealed class DataRow
{
    /// <summary>Kolom -> nilai (di-render string).</summary>
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(string column) =>
        Values.TryGetValue(column, out var v) ? v : null;

    public void Set(string column, string? value)
    {
        if (value is null) Values.Remove(column);
        else Values[column] = value;
    }
}
