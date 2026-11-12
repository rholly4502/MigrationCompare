using System.Globalization;
using MigrationCompare.Core.Models;
using MigrationCompare.Core.Sources;

namespace MigrationCompare.Core.Sources;

/// <summary>Membaca data dari file CSV (baris pertama = header).</summary>
public sealed class CsvSource : IDataSource
{
    private readonly DataSourceDefinition _def;
    private StreamReader? _reader;
    private string[]? _headers;
    private string? _peek;

    public string Name => _def.Name;

    public CsvSource(DataSourceDefinition def)
    {
        if (def.Kind != "csv") throw new ArgumentException("Bukan sumber CSV.", nameof(def));
        _def = def;
    }

    public IReadOnlyList<string> Open()
    {
        var path = _def.Connection;
        if (!File.Exists(path)) throw new FileNotFoundException($"File CSV tidak ditemukan: {path}", path);

        _reader = new StreamReader(path);
        var headerLine = _reader.ReadLine()
            ?? throw new InvalidDataException("File CSV kosong (tidak ada header).");
        _headers = SplitCsv(headerLine);
        _peek = _reader.ReadLine(); // sudah siapkan baris pertama
        return _headers;
    }

    public DataRow? ReadRow()
    {
        var line = _peek;
        if (line is null) return null;

        _peek = _reader!.ReadLine();
        var fields = SplitCsv(line);

        var row = new DataRow();
        for (int i = 0; i < _headers!.Length; i++)
        {
            if (i < fields.Length) row.Set(_headers[i], fields[i]);
        }
        return row;
    }

    private static string[] SplitCsv(string line)
    {
        // Split sederhana (tidak menangani quoted comma penuh; cukup untuk v1).
        return line.Split(',', StringSplitOptions.TrimEntries);
    }

    public void Dispose() => _reader?.Dispose();
}
