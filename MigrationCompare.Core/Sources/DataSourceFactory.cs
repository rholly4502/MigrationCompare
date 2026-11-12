using MigrationCompare.Core.Models;
using MigrationCompare.Core.Sources;

namespace MigrationCompare.Core.Sources;

/// <summary>Factory untuk membuat sumber data berdasarkan definisi.</summary>
public static class DataSourceFactory
{
    public static IDataSource Create(DataSourceDefinition def) =>
        def.Kind.ToLowerInvariant() switch
        {
            "sql" => new SqlSource(def),
            "csv" => new CsvSource(def),
            _ => throw new ArgumentException($"Jenis sumber tidak dikenal: {def.Kind}")
        };
}
