using System.Globalization;
using Microsoft.Data.SqlClient;
using MigrationCompare.Core.Models;

namespace MigrationCompare.Core.Sources;

/// <summary>Membaca data dari SQL Server via query SELECT.</summary>
public sealed class SqlSource : IDataSource
{
    private readonly DataSourceDefinition _def;
    private SqlConnection? _connection;
    private SqlCommand? _command;
    private SqlDataReader? _reader;
    private Dictionary<string, int>? _columnIndex;
    private int _fieldCount;

    public string Name => _def.Name;

    public SqlSource(DataSourceDefinition def)
    {
        if (!string.Equals(def.Kind, "sql", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Bukan sumber SQL.", nameof(def));
        if (string.IsNullOrWhiteSpace(def.Query))
            throw new ArgumentException("Query kosong.", nameof(def));
        _def = def;
    }

    public IReadOnlyList<string> Open()
    {
        Dispose();

        _connection = new SqlConnection(_def.Connection);
        _connection.Open();

        _command = new SqlCommand(_def.Query, _connection)
        {
            CommandTimeout = 0 // Hindari timeout pada query skala jutaan baris
        };
        _reader = _command.ExecuteReader();

        _fieldCount = _reader.FieldCount;
        var columns = new List<string>(_fieldCount);
        _columnIndex = new Dictionary<string, int>(_fieldCount, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < _fieldCount; i++)
        {
            var colName = _reader.GetName(i);
            columns.Add(colName);
            _columnIndex.TryAdd(colName, i);
        }

        return columns;
    }

    public DataRow? ReadRow()
    {
        if (_reader is null || !_reader.Read()) return null;

        var values = new string?[_fieldCount];
        for (int i = 0; i < _fieldCount; i++)
        {
            if (!_reader.IsDBNull(i))
            {
                values[i] = Convert.ToString(_reader.GetValue(i), CultureInfo.InvariantCulture);
            }
        }
        return new DataRow(_columnIndex!, values);
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _reader = null;
        _command?.Dispose();
        _command = null;
        _connection?.Dispose();
        _connection = null;
    }
}
