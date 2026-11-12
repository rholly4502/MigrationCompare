using Microsoft.Data.SqlClient;
using MigrationCompare.Core.Models;
using MigrationCompare.Core.Sources;

namespace MigrationCompare.Core.Sources;

/// <summary>Membaca data dari SQL Server via query SELECT.</summary>
public sealed class SqlSource : IDataSource
{
    private readonly DataSourceDefinition _def;
    private SqlConnection? _connection;
    private SqlDataReader? _reader;

    public string Name => _def.Name;

    public SqlSource(DataSourceDefinition def)
    {
        if (def.Kind != "sql") throw new ArgumentException("Bukan sumber SQL.", nameof(def));
        if (string.IsNullOrWhiteSpace(def.Query)) throw new ArgumentException("Query kosong.", nameof(def));
        _def = def;
    }

    public IReadOnlyList<string> Open()
    {
        _connection = new SqlConnection(_def.Connection);
        _connection.Open();

        using var cmd = new SqlCommand(_def.Query, _connection);
        _reader = cmd.ExecuteReader();

        var columns = new List<string>();
        for (int i = 0; i < _reader.FieldCount; i++) columns.Add(_reader.GetName(i));
        return columns;
    }

    public DataRow? ReadRow()
    {
        if (_reader is null || !_reader.Read()) return null;

        var row = new DataRow();
        for (int i = 0; i < _reader.FieldCount; i++)
        {
            row.Set(_reader.GetName(i), _reader.IsDBNull(i) ? null : _reader.GetValue(i).ToString());
        }
        return row;
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _connection?.Dispose();
    }
}
