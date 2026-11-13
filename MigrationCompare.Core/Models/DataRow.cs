namespace MigrationCompare.Core.Models;

/// <summary>Representasi satu baris data dari sumber mana pun (SQL/CSV).</summary>
public sealed class DataRow
{
    private readonly IReadOnlyDictionary<string, int>? _columnIndex;
    private readonly string?[]? _values;
    private Dictionary<string, string>? _dict;

    public DataRow()
    {
        _dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public DataRow(IReadOnlyDictionary<string, int> columnIndex, string?[] values)
    {
        _columnIndex = columnIndex;
        _values = values;
    }

    /// <summary>Kolom -> nilai (di-render string). Materialisasi lazy untuk baris kompak.</summary>
    public Dictionary<string, string> Values
    {
        get => EnsureDictionary();
        set => _dict = value;
    }

    public string? Get(string column)
    {
        if (_dict is not null)
            return _dict.TryGetValue(column, out var v) ? v : null;

        if (_columnIndex is not null && _values is not null && _columnIndex.TryGetValue(column, out var idx))
            return idx < _values.Length ? _values[idx] : null;

        return null;
    }

    public void Set(string column, string? value)
    {
        if (_dict is null && _columnIndex is not null && _values is not null &&
            _columnIndex.TryGetValue(column, out var idx) && idx < _values.Length)
        {
            _values[idx] = value;
            return;
        }

        var dict = EnsureDictionary();
        if (value is null) dict.Remove(column);
        else dict[column] = value;
    }

    /// <summary>Mengiterasi pasangan kolom-nilai non-null tanpa alokasi Dictionary baru.</summary>
    public IEnumerable<KeyValuePair<string, string>> EnumerateValues()
    {
        if (_dict is not null)
        {
            foreach (var kv in _dict)
                yield return kv;
            yield break;
        }

        if (_columnIndex is null || _values is null) yield break;

        foreach (var kv in _columnIndex)
        {
            if (kv.Value < _values.Length && _values[kv.Value] is { } val)
                yield return new KeyValuePair<string, string>(kv.Key, val);
        }
    }

    private Dictionary<string, string> EnsureDictionary()
    {
        if (_dict is not null) return _dict;

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_columnIndex is not null && _values is not null)
        {
            foreach (var kv in _columnIndex)
            {
                if (kv.Value < _values.Length && _values[kv.Value] is { } val)
                    dict[kv.Key] = val;
            }
        }
        _dict = dict;
        return dict;
    }
}
