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
    private Func<SqlDataReader, int, string?>[] _getters = Array.Empty<Func<SqlDataReader, int, string?>>();

    public string Name => _def.Name;

    public SqlSource(DataSourceDefinition def)
    {
        if (!string.Equals(def.Kind, "sql", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Bukan sumber SQL.", nameof(def));
        if (string.IsNullOrWhiteSpace(def.Query))
            throw new ArgumentException("Query kosong.", nameof(def));
        _def = def;
    }

    /// <summary>Membangun connection string final dari field terstruktur dan/atau connection string manual.</summary>
    public static string ResolveConnectionString(DataSourceDefinition def)
    {
        var builder = string.IsNullOrWhiteSpace(def.Connection)
            ? new SqlConnectionStringBuilder()
            : new SqlConnectionStringBuilder(def.Connection);

        if (!string.IsNullOrWhiteSpace(def.Server))
            builder.DataSource = def.Server.Trim();

        if (!string.IsNullOrWhiteSpace(def.Database))
            builder.InitialCatalog = def.Database.Trim();

        if (!string.IsNullOrWhiteSpace(def.Username))
        {
            builder.Remove("Trusted_Connection");
            builder.Remove("Integrated Security");
            builder.IntegratedSecurity = false;
            builder.UserID = def.Username.Trim();
            builder.Password = def.Password;
        }
        else if (def.UseWindowsAuth)
        {
            builder.IntegratedSecurity = true;
        }

        if (!builder.ContainsKey("TrustServerCertificate"))
            builder.TrustServerCertificate = true;

        if (string.IsNullOrWhiteSpace(builder.DataSource))
            throw new InvalidOperationException($"'{def.Name}': Server atau Connection String SQL wajib diisi.");

        return builder.ConnectionString;
    }

    /// <summary>Menguji koneksi ke SQL Server dan memvalidasi skema query (jika diisi) tanpa menarik seluruh baris.</summary>
    public static async Task<(string server, string database, IReadOnlyList<string> columns)> TestConnectionAsync(
        DataSourceDefinition def,
        int timeoutSeconds = 10,
        CancellationToken cancellationToken = default)
    {
        var connStr = ResolveConnectionString(def);
        var builder = new SqlConnectionStringBuilder(connStr)
        {
            ConnectTimeout = timeoutSeconds
        };

        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        var columns = new List<string>();
        if (!string.IsNullOrWhiteSpace(def.Query))
        {
            await using var cmd = new SqlCommand(def.Query, conn)
            {
                CommandTimeout = timeoutSeconds
            };
            await using var reader = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SchemaOnly, cancellationToken);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                columns.Add(reader.GetName(i));
            }
        }

        return (conn.DataSource, conn.Database, columns);
    }

    /// <summary>Memilih getter tercepat untuk tiap kolom berdasarkan tipe field (hindari boxing GetValue).</summary>
    private static Func<SqlDataReader, int, string?> CreateGetter(Type ft)
    {
        if (ft == typeof(string))
            return (r, i) => r.GetString(i);

        if (ft == typeof(decimal))
            return (r, i) => r.GetDecimal(i).ToString(CultureInfo.InvariantCulture);
        if (ft == typeof(double))
            return (r, i) => r.GetDouble(i).ToString(CultureInfo.InvariantCulture);
        if (ft == typeof(float))
            return (r, i) => r.GetFloat(i).ToString(CultureInfo.InvariantCulture);

        if (ft == typeof(byte))
            return (r, i) => r.GetByte(i).ToString(CultureInfo.InvariantCulture);
        if (ft == typeof(sbyte))
            return (r, i) => r.GetByte(i).ToString(CultureInfo.InvariantCulture);
        if (ft == typeof(short))
            return (r, i) => r.GetInt16(i).ToString(CultureInfo.InvariantCulture);
        if (ft == typeof(ushort))
            return (r, i) => r.GetInt16(i).ToString(CultureInfo.InvariantCulture);
        if (ft == typeof(int))
            return (r, i) => r.GetInt32(i).ToString(CultureInfo.InvariantCulture);
        if (ft == typeof(uint))
            return (r, i) => r.GetInt32(i).ToString(CultureInfo.InvariantCulture);
        if (ft == typeof(long))
            return (r, i) => r.GetInt64(i).ToString(CultureInfo.InvariantCulture);
        if (ft == typeof(ulong))
            return (r, i) => r.GetInt64(i).ToString(CultureInfo.InvariantCulture);

        if (ft == typeof(bool))
            return (r, i) => r.GetBoolean(i) ? "true" : "false";

        if (ft == typeof(DateTime))
            return (r, i) => r.GetDateTime(i).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        if (ft == typeof(DateTimeOffset))
            return (r, i) => r.GetDateTimeOffset(i).ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

        if (ft == typeof(Guid))
            return (r, i) => r.GetGuid(i).ToString("D", CultureInfo.InvariantCulture);

        if (ft == typeof(byte[]))
            return (r, i) => Convert.ToBase64String((byte[])r.GetValue(i));

        // Fallback: hindari Convert.ToString pada null di atas.
        return (r, i) =>
        {
            var v = r.GetValue(i);
            return v is null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
        };
    }

    public IReadOnlyList<string> Open()
    {
        Dispose();

        _connection = new SqlConnection(ResolveConnectionString(_def));
        _connection.Open();

        _command = new SqlCommand(_def.Query, _connection)
        {
            CommandTimeout = 0 // Hindari timeout pada query skala jutaan baris
        };
        _reader = _command.ExecuteReader();

        _fieldCount = _reader.FieldCount;
        var columns = new List<string>(_fieldCount);
        _columnIndex = new Dictionary<string, int>(_fieldCount, StringComparer.OrdinalIgnoreCase);

        var getters = new Func<SqlDataReader, int, string?>[_fieldCount];
        for (int i = 0; i < _fieldCount; i++)
        {
            var colName = _reader.GetName(i);
            columns.Add(colName);
            _columnIndex.TryAdd(colName, i);
            getters[i] = CreateGetter(_reader.GetFieldType(i));
        }
        _getters = getters;

        return columns;
    }

    public DataRow? ReadRow()
    {
        if (_reader is null || !_reader.Read()) return null;

        var values = new string?[_fieldCount];
        var getters = _getters;
        for (int i = 0; i < _fieldCount; i++)
        {
            if (!_reader.IsDBNull(i))
            {
                values[i] = getters[i](_reader, i);
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
