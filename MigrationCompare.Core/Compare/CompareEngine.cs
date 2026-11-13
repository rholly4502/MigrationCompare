using System.Globalization;
using MigrationCompare.Core.Models;
using MigrationCompare.Core.Sources;

namespace MigrationCompare.Core.Compare;

/// <summary>Engine komparasi dua sumber berbasis key join + banding value.</summary>
public sealed class CompareEngine
{
    private readonly DataSourceDefinition _a;
    private readonly DataSourceDefinition _b;

    /// <summary>Kolom kunci dari sumber B (jika tidak diisi, pakai yang dari A).</summary>
    private readonly IReadOnlyList<string> _bKeys;

    /// <summary>Toleransi perbandingan numerik (0 = harus sama persis).</summary>
    private readonly decimal _numericTolerance;

    /// <summary>Jika true, gunakan Streaming Sort-Merge Join (RAM konstan O(1)).
    /// Kedua sumber harus sudah terurut menaik (ORDER BY) berdasarkan kolom kunci.</summary>
    public bool AssumeSortedByKey { get; init; }

    /// <summary>Derajat paralelisasi baca A∥B (A = off, >=2 = baca kedua sumber bersamaan).
    /// Ketika &gt;1, hash-join membuffer baris B; merge membuffer A &amp; B. Trade-off RAM vs kecepatan.</summary>
    public int ParallelLoad { get; init; } = 1;

    /// <summary>Auto-split sumber SQL besar menjadi N segmen range (NTILE) saat ParallelLoad aktif.
    /// Tool menghitung COUNT &amp; boundary sendiri; pengguna hanya men-set jumlah segmen.
    /// Berlaku untuk sumber SQL dengan satu kolom kunci. &gt;1 = aktif.</summary>
    public int SplitSegments { get; init; } = 1;

    /// <summary>Batas maksimal baris hasil yang disimpan di memori (Rows).</summary>
    public int MaxStoredRows { get; init; } = 100_000;

    /// <summary>Batas maksimal baris yang identik (Match tanpa beda nilai) yang disimpan di Rows.</summary>
    public int MaxStoredIdenticalRows { get; init; } = 10_000;

    public CompareEngine(DataSourceDefinition a, DataSourceDefinition b, decimal numericTolerance = 0m)
    {
        if (a.KeyColumns.Count == 0) throw new ArgumentException("Sumber A harus punya minimal satu kolom kunci.", nameof(a));
        _a = a;
        _b = b;
        _bKeys = b.KeyColumns.Count > 0 ? b.KeyColumns : a.KeyColumns;
        _numericTolerance = numericTolerance;
    }

    /// <summary>Menjalankan komparasi dan mengembalikan hasil lengkap.</summary>
    public CompareResult Compare() => Compare(null, default);

    /// <summary>Menjalankan komparasi dengan dukungan laporan progres dan pembatalan.</summary>
    public CompareResult Compare(IProgress<(long rowsA, long rowsB)>? progress, CancellationToken cancellationToken = default)
    {
        using var sourceA = DataSourceFactory.Create(_a);
        using var sourceB = DataSourceFactory.Create(_b);

        var colsA = sourceA.Open();
        var colsB = sourceB.Open();

        var compareCols = ResolveCompareColumns(colsA, colsB);
        var result = new CompareResult { CompareColumns = compareCols.ToList() };

        if (ShouldSplit())
        {
            CompareSplit(result, progress, cancellationToken);
        }
        else if (ParallelLoad >= 2)
        {
            CompareParallel(sourceA, sourceB, compareCols, result, progress, cancellationToken);
        }
        else if (AssumeSortedByKey)
        {
            CompareSortedMerge(sourceA, sourceB, compareCols, result, progress, cancellationToken);
        }
        else
        {
            CompareHashJoin(sourceA, sourceB, compareCols, result, progress, cancellationToken);
        }

        return result;
    }

    private bool ShouldSplit() =>
        ParallelLoad >= 2 && SplitSegments > 1 &&
        string.Equals(_a.Kind, "sql", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(_b.Kind, "sql", StringComparison.OrdinalIgnoreCase) &&
        _a.KeyColumns.Count == 1 && _bKeys.Count == 1;

    // ===== Jalur auto-split: bagi sumber SQL besar menjadi N segmen, baca semua paralel =====

    private void CompareSplit(
        CompareResult result,
        IProgress<(long rowsA, long rowsB)>? progress,
        CancellationToken cancellationToken)
    {
        var segA = SqlSegmenter.AnalyzeAsync(_a, _a.KeyColumns[0], SplitSegments, cancellationToken)
            .GetAwaiter().GetResult();
        var segB = SqlSegmenter.AnalyzeAsync(_b, _bKeys[0], SplitSegments, cancellationToken)
            .GetAwaiter().GetResult();

        var numA = SqlSegmenter.DetectNumericKeyAsync(_a, _a.KeyColumns[0], cancellationToken)
            .GetAwaiter().GetResult();
        var numB = SqlSegmenter.DetectNumericKeyAsync(_b, _bKeys[0], cancellationToken)
            .GetAwaiter().GetResult();

        var defsA = SqlSegmenter.BuildSegmentDefinitions(_a, segA, numA);
        var defsB = SqlSegmenter.BuildSegmentDefinitions(_b, segB, numB);

        // Kolom: ambil dari segmen pertama tiap sisi (skema identik antar-segmen).
        var colsA = OpenColumns(defsA[0]);
        var colsB = OpenColumns(defsB[0]);
        var compareCols = ResolveCompareColumns(colsA, colsB);
        result.CompareColumns = compareCols.ToList();

        var tasks = new List<Task<List<DataRow>>>();
        foreach (var d in defsA)
        {
            tasks.Add(Task.Run(() => ReadSegment(d, progress, cancellationToken), cancellationToken));
        }
        foreach (var d in defsB)
        {
            tasks.Add(Task.Run(() => ReadSegment(d, progress, cancellationToken), cancellationToken));
        }

        try
        {
            Task.WaitAll(tasks.ToArray());
        }
        catch (AggregateException ex)
        {
            ex.Handle(e => e is OperationCanceledException);
            throw new OperationCanceledException(cancellationToken);
        }

        var rowsA = new List<DataRow>();
        for (int i = 0; i < defsA.Count; i++) rowsA.AddRange(tasks[i].Result);

        var rowsB = new List<DataRow>();
        for (int i = defsA.Count; i < tasks.Count; i++) rowsB.AddRange(tasks[i].Result);

        if (AssumeSortedByKey)
        {
            CompareSortedMergeFromLists(rowsA, rowsB, compareCols, result, cancellationToken);
        }
        else
        {
            CompareHashJoinFromLists(rowsA, rowsB, compareCols, result, cancellationToken);
        }
    }

    private static IReadOnlyList<string> OpenColumns(DataSourceDefinition def)
    {
        using var source = DataSourceFactory.Create(def);
        return source.Open();
    }

    private static List<DataRow> ReadSegment(
        DataSourceDefinition def,
        IProgress<(long rowsA, long rowsB)>? progress,
        CancellationToken cancellationToken)
    {
        using var source = DataSourceFactory.Create(def);
        source.Open();
        return ReadAllRows(source, progress, cancellationToken);
    }

    // ===== Jalur paralel: baca A & B bersamaan, lalu proses =====

    private void CompareParallel(
        IDataSource sourceA,
        IDataSource sourceB,
        IReadOnlyList<string> compareCols,
        CompareResult result,
        IProgress<(long rowsA, long rowsB)>? progress,
        CancellationToken cancellationToken)
    {
        // Baca kedua sumber serentak. Tiap source hanya disentuh satu thread (koneksi terpisah).
        var taskA = Task.Run(() => ReadAllRows(sourceA, progress, cancellationToken), cancellationToken);
        var taskB = Task.Run(() => ReadAllRows(sourceB, progress, cancellationToken), cancellationToken);

        try
        {
            Task.WaitAll(taskA, taskB);
        }
        catch (AggregateException ex)
        {
            // Terjemahkan pembatalan kembali menjadi OperationCanceledException agar rapi.
            ex.Handle(e => e is OperationCanceledException);
            throw new OperationCanceledException(cancellationToken);
        }

        var rowsA = taskA.Result;
        var rowsB = taskB.Result;

        if (AssumeSortedByKey)
        {
            CompareSortedMergeFromLists(rowsA, rowsB, compareCols, result, cancellationToken);
        }
        else
        {
            CompareHashJoinFromLists(rowsA, rowsB, compareCols, result, cancellationToken);
        }
    }

    private static List<DataRow> ReadAllRows(
        IDataSource source,
        IProgress<(long rowsA, long rowsB)>? progress,
        CancellationToken cancellationToken)
    {
        var rows = new List<DataRow>();
        long count = 0;
        while (source.ReadRow() is { } row)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(row);
            count++;
            if ((count & 0x3FFF) == 0) progress?.Report((count, 0));
        }
        progress?.Report((count, 0));
        return rows;
    }

    private void CompareHashJoinFromLists(
        List<DataRow> rowsA,
        List<DataRow> rowsB,
        IReadOnlyList<string> compareCols,
        CompareResult result,
        CancellationToken cancellationToken)
    {
        var mapA = new Dictionary<string, DataRow?>(StringComparer.OrdinalIgnoreCase);
        int totalA = 0, totalB = 0, totalMatched = 0, totalMissingInA = 0, totalMissingInB = 0, totalDiffValues = 0;
        int storedIdentical = 0;

        foreach (var rowA in rowsA)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = BuildKey(rowA, _a.KeyColumns);
            if (mapA.TryAdd(key, rowA)) totalA++;
        }

        foreach (var rowB in rowsB)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = BuildKey(rowB, _bKeys);

            if (mapA.TryGetValue(key, out var rowA))
            {
                if (rowA is null) continue;
                mapA[key] = null;
                totalB++;
                totalMatched++;

                var diffCols = FindDifferingColumns(rowA, rowB, compareCols);
                if (diffCols.Count > 0)
                {
                    totalDiffValues++;
                    TryAddRow(result, new CompareResultRow
                    {
                        Key = key,
                        Status = RowStatus.Match,
                        RowA = rowA,
                        RowB = rowB,
                        DifferingColumns = diffCols
                    });
                }
                else if (storedIdentical < MaxStoredIdenticalRows && result.Rows.Count < MaxStoredRows)
                {
                    storedIdentical++;
                    result.Rows.Add(new CompareResultRow
                    {
                        Key = key,
                        Status = RowStatus.Match,
                        RowA = rowA,
                        RowB = rowB
                    });
                }
            }
            else
            {
                mapA[key] = null;
                totalB++;
                totalMissingInA++;
                TryAddRow(result, new CompareResultRow
                {
                    Key = key,
                    Status = RowStatus.MissingInA,
                    RowB = rowB
                });
            }
        }

        foreach (var kv in mapA)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (kv.Value is null) continue;
            totalMissingInB++;
            TryAddRow(result, new CompareResultRow
            {
                Key = kv.Key,
                Status = RowStatus.MissingInB,
                RowA = kv.Value
            });
        }

        result.TotalA = totalA;
        result.TotalB = totalB;
        result.TotalMatched = totalMatched;
        result.TotalMissingInA = totalMissingInA;
        result.TotalMissingInB = totalMissingInB;
        result.TotalDiffValues = totalDiffValues;
    }

    private void CompareSortedMergeFromLists(
        List<DataRow> rowsA,
        List<DataRow> rowsB,
        IReadOnlyList<string> compareCols,
        CompareResult result,
        CancellationToken cancellationToken)
    {
        int idxA = 0, idxB = 0;
        long rowsA2 = 0, rowsB2 = 0;
        int totalA = 0, totalB = 0, totalMatched = 0, totalMissingInA = 0, totalMissingInB = 0, totalDiffValues = 0;
        int storedIdentical = 0;

        string? lastKeyA = null, lastKeyB = null;

        bool hasA = TryReadNextSortedList(rowsA, ref idxA, _a.KeyColumns, ref lastKeyA, ref rowsA2, out var rowA, out var keyA);
        bool hasB = TryReadNextSortedList(rowsB, ref idxB, _bKeys, ref lastKeyB, ref rowsB2, out var rowB, out var keyB);

        while (hasA || hasB)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int cmp = (hasA, hasB) switch
            {
                (true, true) => StringComparer.OrdinalIgnoreCase.Compare(keyA, keyB),
                (true, false) => -1,
                (false, true) => 1,
                _ => 0
            };

            if (cmp == 0)
            {
                totalA++;
                totalB++;
                totalMatched++;

                var diffCols = FindDifferingColumns(rowA!, rowB!, compareCols);
                if (diffCols.Count > 0)
                {
                    totalDiffValues++;
                    TryAddRow(result, new CompareResultRow
                    {
                        Key = keyA!,
                        Status = RowStatus.Match,
                        RowA = rowA,
                        RowB = rowB,
                        DifferingColumns = diffCols
                    });
                }
                else if (storedIdentical < MaxStoredIdenticalRows && result.Rows.Count < MaxStoredRows)
                {
                    storedIdentical++;
                    result.Rows.Add(new CompareResultRow
                    {
                        Key = keyA!,
                        Status = RowStatus.Match,
                        RowA = rowA,
                        RowB = rowB
                    });
                }

                hasA = TryReadNextSortedList(rowsA, ref idxA, _a.KeyColumns, ref lastKeyA, ref rowsA2, out rowA, out keyA);
                hasB = TryReadNextSortedList(rowsB, ref idxB, _bKeys, ref lastKeyB, ref rowsB2, out rowB, out keyB);
            }
            else if (cmp < 0)
            {
                totalA++;
                totalMissingInB++;
                TryAddRow(result, new CompareResultRow
                {
                    Key = keyA!,
                    Status = RowStatus.MissingInB,
                    RowA = rowA
                });

                hasA = TryReadNextSortedList(rowsA, ref idxA, _a.KeyColumns, ref lastKeyA, ref rowsA2, out rowA, out keyA);
            }
            else
            {
                totalB++;
                totalMissingInA++;
                TryAddRow(result, new CompareResultRow
                {
                    Key = keyB!,
                    Status = RowStatus.MissingInA,
                    RowB = rowB
                });

                hasB = TryReadNextSortedList(rowsB, ref idxB, _bKeys, ref lastKeyB, ref rowsB2, out rowB, out keyB);
            }
        }

        result.TotalA = totalA;
        result.TotalB = totalB;
        result.TotalMatched = totalMatched;
        result.TotalMissingInA = totalMissingInA;
        result.TotalMissingInB = totalMissingInB;
        result.TotalDiffValues = totalDiffValues;
    }

    private static bool TryReadNextSortedList(
        List<DataRow> rows,
        ref int idx,
        IReadOnlyList<string> keyColumns,
        ref string? lastKey,
        ref long rawRows,
        out DataRow? row,
        out string? key)
    {
        while (idx < rows.Count)
        {
            var nextRow = rows[idx++];
            rawRows++;
            var nextKey = BuildKey(nextRow, keyColumns);

            if (lastKey is not null)
            {
                int cmp = StringComparer.OrdinalIgnoreCase.Compare(nextKey, lastKey);
                if (cmp == 0) continue;
                if (cmp < 0)
                {
                    throw new InvalidOperationException(
                        $"Data tidak terurut menaik berdasarkan key ('{nextKey}' muncul setelah '{lastKey}'). " +
                        "Pastikan query menggunakan ORDER BY kolom kunci atau nonaktifkan Mode Streaming.");
                }
            }

            lastKey = nextKey;
            row = nextRow;
            key = nextKey;
            return true;
        }

        row = null;
        key = null;
        return false;
    }

    // ===== Jalur serial (asli) =====

    private void CompareHashJoin(
        IDataSource sourceA,
        IDataSource sourceB,
        IReadOnlyList<string> compareCols,
        CompareResult result,
        IProgress<(long rowsA, long rowsB)>? progress,
        CancellationToken cancellationToken)
    {
        var mapA = new Dictionary<string, DataRow?>(StringComparer.OrdinalIgnoreCase);
        long rowsA = 0, rowsB = 0;
        int totalA = 0, totalB = 0, totalMatched = 0, totalMissingInA = 0, totalMissingInB = 0, totalDiffValues = 0;
        int storedIdentical = 0;

        while (sourceA.ReadRow() is { } rowA)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowsA++;
            var key = BuildKey(rowA, _a.KeyColumns);
            if (mapA.TryAdd(key, rowA)) totalA++;

            if ((rowsA & 0x3FFF) == 0) progress?.Report((rowsA, 0));
        }

        while (sourceB.ReadRow() is { } rowB)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowsB++;
            var key = BuildKey(rowB, _bKeys);

            if (mapA.TryGetValue(key, out var rowA))
            {
                if (rowA is null) continue;
                mapA[key] = null;
                totalB++;
                totalMatched++;

                var diffCols = FindDifferingColumns(rowA, rowB, compareCols);
                if (diffCols.Count > 0)
                {
                    totalDiffValues++;
                    TryAddRow(result, new CompareResultRow
                    {
                        Key = key,
                        Status = RowStatus.Match,
                        RowA = rowA,
                        RowB = rowB,
                        DifferingColumns = diffCols
                    });
                }
                else if (storedIdentical < MaxStoredIdenticalRows && result.Rows.Count < MaxStoredRows)
                {
                    storedIdentical++;
                    result.Rows.Add(new CompareResultRow
                    {
                        Key = key,
                        Status = RowStatus.Match,
                        RowA = rowA,
                        RowB = rowB
                    });
                }
            }
            else
            {
                mapA[key] = null;
                totalB++;
                totalMissingInA++;
                TryAddRow(result, new CompareResultRow
                {
                    Key = key,
                    Status = RowStatus.MissingInA,
                    RowB = rowB
                });
            }

            if ((rowsB & 0x3FFF) == 0) progress?.Report((rowsA, rowsB));
        }

        foreach (var kv in mapA)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (kv.Value is null) continue;
            totalMissingInB++;
            TryAddRow(result, new CompareResultRow
            {
                Key = kv.Key,
                Status = RowStatus.MissingInB,
                RowA = kv.Value
            });
        }

        result.TotalA = totalA;
        result.TotalB = totalB;
        result.TotalMatched = totalMatched;
        result.TotalMissingInA = totalMissingInA;
        result.TotalMissingInB = totalMissingInB;
        result.TotalDiffValues = totalDiffValues;
        progress?.Report((rowsA, rowsB));
    }

    private void CompareSortedMerge(
        IDataSource sourceA,
        IDataSource sourceB,
        IReadOnlyList<string> compareCols,
        CompareResult result,
        IProgress<(long rowsA, long rowsB)>? progress,
        CancellationToken cancellationToken)
    {
        long rowsA = 0, rowsB = 0;
        int totalA = 0, totalB = 0, totalMatched = 0, totalMissingInA = 0, totalMissingInB = 0, totalDiffValues = 0;
        int storedIdentical = 0;
        string? lastKeyA = null, lastKeyB = null;

        bool hasA = TryReadNextSorted(sourceA, _a.KeyColumns, ref lastKeyA, ref rowsA, out var rowA, out var keyA, cancellationToken);
        bool hasB = TryReadNextSorted(sourceB, _bKeys, ref lastKeyB, ref rowsB, out var rowB, out var keyB, cancellationToken);

        while (hasA || hasB)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int cmp = (hasA, hasB) switch
            {
                (true, true) => StringComparer.OrdinalIgnoreCase.Compare(keyA, keyB),
                (true, false) => -1,
                (false, true) => 1,
                _ => 0
            };

            if (cmp == 0)
            {
                totalA++;
                totalB++;
                totalMatched++;

                var diffCols = FindDifferingColumns(rowA!, rowB!, compareCols);
                if (diffCols.Count > 0)
                {
                    totalDiffValues++;
                    TryAddRow(result, new CompareResultRow
                    {
                        Key = keyA!,
                        Status = RowStatus.Match,
                        RowA = rowA,
                        RowB = rowB,
                        DifferingColumns = diffCols
                    });
                }
                else if (storedIdentical < MaxStoredIdenticalRows && result.Rows.Count < MaxStoredRows)
                {
                    storedIdentical++;
                    result.Rows.Add(new CompareResultRow
                    {
                        Key = keyA!,
                        Status = RowStatus.Match,
                        RowA = rowA,
                        RowB = rowB
                    });
                }

                hasA = TryReadNextSorted(sourceA, _a.KeyColumns, ref lastKeyA, ref rowsA, out rowA, out keyA, cancellationToken);
                hasB = TryReadNextSorted(sourceB, _bKeys, ref lastKeyB, ref rowsB, out rowB, out keyB, cancellationToken);
            }
            else if (cmp < 0)
            {
                totalA++;
                totalMissingInB++;
                TryAddRow(result, new CompareResultRow
                {
                    Key = keyA!,
                    Status = RowStatus.MissingInB,
                    RowA = rowA
                });

                hasA = TryReadNextSorted(sourceA, _a.KeyColumns, ref lastKeyA, ref rowsA, out rowA, out keyA, cancellationToken);
            }
            else
            {
                totalB++;
                totalMissingInA++;
                TryAddRow(result, new CompareResultRow
                {
                    Key = keyB!,
                    Status = RowStatus.MissingInA,
                    RowB = rowB
                });

                hasB = TryReadNextSorted(sourceB, _bKeys, ref lastKeyB, ref rowsB, out rowB, out keyB, cancellationToken);
            }

            if (((rowsA + rowsB) & 0x3FFF) == 0) progress?.Report((rowsA, rowsB));
        }

        result.TotalA = totalA;
        result.TotalB = totalB;
        result.TotalMatched = totalMatched;
        result.TotalMissingInA = totalMissingInA;
        result.TotalMissingInB = totalMissingInB;
        result.TotalDiffValues = totalDiffValues;
        progress?.Report((rowsA, rowsB));
    }

    private static bool TryReadNextSorted(
        IDataSource source,
        IReadOnlyList<string> keyColumns,
        ref string? lastKey,
        ref long rawRows,
        out DataRow? row,
        out string? key,
        CancellationToken cancellationToken)
    {
        while (source.ReadRow() is { } nextRow)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rawRows++;
            var nextKey = BuildKey(nextRow, keyColumns);

            if (lastKey is not null)
            {
                int cmp = StringComparer.OrdinalIgnoreCase.Compare(nextKey, lastKey);
                if (cmp == 0) continue;
                if (cmp < 0)
                {
                    throw new InvalidOperationException(
                        $"Data pada '{source.Name}' tidak terurut menaik berdasarkan key ('{nextKey}' muncul setelah '{lastKey}'). " +
                        "Pastikan query menggunakan ORDER BY kolom kunci atau nonaktifkan Mode Streaming.");
                }
            }

            lastKey = nextKey;
            row = nextRow;
            key = nextKey;
            return true;
        }

        row = null;
        key = null;
        return false;
    }

    private void TryAddRow(CompareResult result, CompareResultRow row)
    {
        if (result.Rows.Count < MaxStoredRows)
        {
            result.Rows.Add(row);
        }
        else
        {
            result.IsTruncated = true;
        }
    }

    private List<string> FindDifferingColumns(DataRow rowA, DataRow rowB, IReadOnlyList<string> compareCols)
    {
        var diffCols = new List<string>();
        foreach (var col in compareCols)
        {
            var va = Normalize(rowA.Get(col));
            var vb = Normalize(rowB.Get(col));
            if (!ValuesEqual(va, vb)) diffCols.Add(col);
        }
        return diffCols;
    }

    private static string BuildKey(DataRow row, IReadOnlyList<string> keys)
    {
        if (keys.Count == 1)
            return row.Get(keys[0]) ?? "";

        var parts = new string[keys.Count];
        for (int i = 0; i < keys.Count; i++)
        {
            parts[i] = row.Get(keys[i]) ?? "";
        }
        return string.Join('\u001F', parts);
    }

    private IReadOnlyList<string> ResolveCompareColumns(IReadOnlyList<string> colsA, IReadOnlyList<string> colsB)
    {
        if (_a.CompareColumns.Count > 0) return _a.CompareColumns;

        var setB = new HashSet<string>(colsB, StringComparer.OrdinalIgnoreCase);
        var resultL = new List<string>();
        foreach (var colA in colsA)
        {
            if (_a.KeyColumns.Any(k => string.Equals(k, colA, StringComparison.OrdinalIgnoreCase))) continue;
            if (setB.Contains(colA)) resultL.Add(colA);
        }
        return resultL;
    }

    private static string? Normalize(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private bool ValuesEqual(string? a, string? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;

        if (decimal.TryParse(a, NumberStyles.Any, CultureInfo.InvariantCulture, out var na) &&
            decimal.TryParse(b, NumberStyles.Any, CultureInfo.InvariantCulture, out var nb))
        {
            return Math.Abs(na - nb) <= _numericTolerance;
        }
        return false;
    }
}
