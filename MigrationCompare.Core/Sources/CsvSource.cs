using System.Text;
using MigrationCompare.Core.Models;

namespace MigrationCompare.Core.Sources;

/// <summary>Membaca data dari file CSV (baris pertama = header).</summary>
public sealed class CsvSource : IDataSource
{
    private readonly DataSourceDefinition _def;
    private StreamReader? _reader;
    private string[]? _headers;
    private Dictionary<string, int>? _columnIndex;
    private string? _peek;

    public string Name => _def.Name;

    public CsvSource(DataSourceDefinition def)
    {
        if (!string.Equals(def.Kind, "csv", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Bukan sumber CSV.", nameof(def));
        _def = def;
    }

    public IReadOnlyList<string> Open()
    {
        _reader?.Dispose();

        var path = _def.Connection;
        if (!File.Exists(path)) throw new FileNotFoundException($"File CSV tidak ditemukan: {path}", path);

        _reader = new StreamReader(path);
        var headerLine = _reader.ReadLine()
            ?? throw new InvalidDataException("File CSV kosong (tidak ada header).");

        _headers = SplitCsv(headerLine).ToArray();
        _columnIndex = new Dictionary<string, int>(_headers.Length, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < _headers.Length; i++)
        {
            _columnIndex.TryAdd(_headers[i], i);
        }

        _peek = _reader.ReadLine(); // sudah siapkan baris pertama
        return _headers;
    }

    public DataRow? ReadRow()
    {
        var line = _peek;
        if (line is null) return null;

        _peek = _reader!.ReadLine();
        var fields = SplitCsv(line);

        var values = new string?[_headers!.Length];
        var count = Math.Min(_headers.Length, fields.Count);
        for (int i = 0; i < count; i++)
        {
            values[i] = fields[i];
        }
        return new DataRow(_columnIndex!, values);
    }

    /// <summary>Memecah baris CSV dengan dukungan tanda kutip ganda dan koma di dalam kutip.</summary>
    internal static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        bool wasQuoted = false;

        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];

            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(ch);
                }
            }
            else
            {
                if (ch == ',' )
                {
                    result.Add(wasQuoted ? sb.ToString() : sb.ToString().Trim());
                    sb.Clear();
                    wasQuoted = false;
                }
                else if (ch == '"' && sb.Length == 0 && !wasQuoted)
                {
                    inQuotes = true;
                    wasQuoted = true;
                }
                else
                {
                    sb.Append(ch);
                }
            }
        }

        result.Add(wasQuoted ? sb.ToString() : sb.ToString().Trim());
        return result;
    }

    public void Dispose() => _reader?.Dispose();
}
