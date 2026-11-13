using MigrationCompare.Core.Compare;
using MigrationCompare.Core.Models;
using MigrationCompare.Core.Quality;
using MigrationCompare.Core.Report;
using MigrationCompare.Core.Sources;

namespace MigrationCompare.Tests;

public class CompareEngineTests
{
    private static DataSourceDefinition Csv(string name, string path, string[] keys, string[] cols) =>
        new()
        {
            Name = name,
            Kind = "csv",
            Connection = path,
            KeyColumns = keys.ToList(),
            CompareColumns = cols.ToList()
        };

    private static string WriteTempCsv(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcompare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "data.csv");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Compare_PerformsKeyJoinAndValueDiff()
    {
        var pathA = WriteTempCsv("BillingNo,Amount,Status\nT001,100.00,Paid\nT002,250.50,Unpaid\nT003,30.00,Paid\n");
        var pathB = WriteTempCsv("BillingNo,Amount,Status\nT001,100.00,Paid\nT002,250.51,Unpaid\nT004,10.00,Paid\n");

        var a = Csv("A", pathA, ["BillingNo"], ["Amount", "Status"]);
        var b = Csv("B", pathB, ["BillingNo"], ["Amount", "Status"]);

        var engine = new CompareEngine(a, b, numericTolerance: 0.01m);
        var result = engine.Compare();

        Assert.Equal(3, result.TotalA);       // T001,T002,T003
        Assert.Equal(3, result.TotalB);       // T001,T002,T004
        Assert.Equal(2, result.TotalMatched); // T001,T002
        Assert.Equal(1, result.TotalMissingInB); // T003
        Assert.Equal(1, result.TotalMissingInA); // T004
        // T002 beda 0.01 (250.50 vs 250.51) -> dianggap sama karena toleransi.
        Assert.Equal(0, result.TotalDiffValues);

        var t001 = result.Rows.Single(r => r.Key == "T001");
        Assert.Equal(RowStatus.Match, t001.Status);
        Assert.Empty(t001.DifferingColumns);
    }

    [Fact]
    public void Compare_NumericToleranceZero_FlagsTinyDiff()
    {
        var pathA = WriteTempCsv("id,amt\n1,10.00\n");
        var pathB = WriteTempCsv("id,amt\n1,10.01\n");

        var a = Csv("A", pathA, ["id"], ["amt"]);
        var b = Csv("B", pathB, ["id"], ["amt"]);

        var engine = new CompareEngine(a, b, numericTolerance: 0m);
        var result = engine.Compare();

        Assert.Equal(1, result.TotalDiffValues);
        Assert.Contains("amt", result.Rows.Single(r => r.Key == "1").DifferingColumns);
    }

    [Fact]
    public void Quality_DetectsNullEmptyAndDuplicates()
    {
        var path = WriteTempCsv("id,name,qty\n1,Alpha,10\n2,,0\n3,Gamma,\n1,Alpha,10\n");
        var def = Csv("S", path, ["id"], ["name", "qty"]);

        var checker = new QualityChecker(def);
        var report = checker.Check();

        Assert.Equal(4, report.TotalRows);
        Assert.Equal(1, report.DuplicateKeys); // id=1 duplikat
        // empty name (baris 2), empty qty (baris 3)
        Assert.Equal(1, report.EmptyPerColumn["name"]);
        Assert.Equal(1, report.EmptyPerColumn["qty"]);
    }

    [Fact]
    public void Compare_StreamingSortedMerge_MatchesExpectedCountsAndDiffs()
    {
        var pathA = WriteTempCsv("BillingNo,Amount,Status\nT001,100.00,Paid\nT001,100.00,Paid\nT002,250.50,Unpaid\nT003,30.00,Paid\n");
        var pathB = WriteTempCsv("BillingNo,Amount,Status\nT001,100.00,Paid\nT002,260.00,Paid\nT004,10.00,Paid\n");

        var a = Csv("A", pathA, ["BillingNo"], ["Amount", "Status"]);
        var b = Csv("B", pathB, ["BillingNo"], ["Amount", "Status"]);

        var engine = new CompareEngine(a, b, numericTolerance: 0m) { AssumeSortedByKey = true };
        var result = engine.Compare();

        Assert.Equal(3, result.TotalA);
        Assert.Equal(3, result.TotalB);
        Assert.Equal(2, result.TotalMatched);
        Assert.Equal(1, result.TotalMissingInB);
        Assert.Equal(1, result.TotalMissingInA);
        Assert.Equal(1, result.TotalDiffValues);

        var t002 = result.Rows.Single(r => r.Key == "T002");
        Assert.Equal(["Amount", "Status"], t002.DifferingColumns);
    }

    [Fact]
    public void Compare_StreamingSortedMerge_ThrowsWhenUnsorted()
    {
        var pathA = WriteTempCsv("id,val\n2,B\n1,A\n");
        var pathB = WriteTempCsv("id,val\n1,A\n2,B\n");

        var a = Csv("A", pathA, ["id"], ["val"]);
        var b = Csv("B", pathB, ["id"], ["val"]);

        var engine = new CompareEngine(a, b) { AssumeSortedByKey = true };
        Assert.Throws<InvalidOperationException>(() => engine.Compare());
    }

    [Fact]
    public void Compare_DuplicateKeysInSourceA_DoesNotProduceDuplicateMissingInB()
    {
        var pathA = WriteTempCsv("id,val\n1,A\n1,A\n2,B\n");
        var pathB = WriteTempCsv("id,val\n2,B\n");

        var a = Csv("A", pathA, ["id"], ["val"]);
        var b = Csv("B", pathB, ["id"], ["val"]);

        var result = new CompareEngine(a, b).Compare();

        Assert.Equal(2, result.TotalA);
        Assert.Equal(1, result.TotalMissingInB);
        Assert.Single(result.Rows, r => r.Status == RowStatus.MissingInB && r.Key == "1");
    }

    [Fact]
    public void Compare_CsvWithQuotedCommas_ParsesFieldsCorrectly()
    {
        var pathA = WriteTempCsv("id,city,note\n1,\"Jakarta, Indonesia\",\"He said \"\"OK\"\"\"\n");
        var pathB = WriteTempCsv("id,city,note\n1,\"Jakarta, Indonesia\",\"He said \"\"OK\"\"\"\n");

        var a = Csv("A", pathA, ["id"], ["city", "note"]);
        var b = Csv("B", pathB, ["id"], ["city", "note"]);

        var result = new CompareEngine(a, b).Compare();

        Assert.Equal(1, result.TotalMatched);
        Assert.Equal(0, result.TotalDiffValues);
        var row = result.Rows.Single();
        Assert.Equal("Jakarta, Indonesia", row.RowA?.Get("city"));
        Assert.Equal("He said \"OK\"", row.RowA?.Get("note"));
    }

    [Fact]
    public void CsvReport_Quality_IncludesDuplicateKeysSection()
    {
        var path = WriteTempCsv("id,name\n1,Alpha\n1,Alpha\n");
        var def = Csv("S", path, ["id"], ["name"]);

        var report = new QualityChecker(def).Check();
        var csv = CsvReport.Quality(report);

        Assert.Contains("kolom,null,kosong", csv);
        Assert.Contains("key_duplikat,jumlah_duplikat_tambahan", csv);
        Assert.Contains("1,1", csv);
    }

    [Fact]
    public void Compare_ParallelLoad_HashJoin_MatchesSerialResults()
    {
        var pathA = WriteTempCsv(
            "BillingNo,Amount,Status\nT001,100.00,Paid\nT002,250.50,Unpaid\nT003,30.00,Paid\nT005,9.00,Paid\n");
        var pathB = WriteTempCsv(
            "BillingNo,Amount,Status\nT001,100.00,Paid\nT002,250.51,Unpaid\nT004,10.00,Paid\nT005,9.00,Refunded\n");

        var a = Csv("A", pathA, ["BillingNo"], ["Amount", "Status"]);
        var b = Csv("B", pathB, ["BillingNo"], ["Amount", "Status"]);

        var serial = new CompareEngine(a, b, numericTolerance: 0.01m).Compare();
        var parallel = new CompareEngine(a, b, numericTolerance: 0.01m) { ParallelLoad = 4 }.Compare();

        Assert.Equal(serial.TotalA, parallel.TotalA);
        Assert.Equal(serial.TotalB, parallel.TotalB);
        Assert.Equal(serial.TotalMatched, parallel.TotalMatched);
        Assert.Equal(serial.TotalMissingInA, parallel.TotalMissingInA);
        Assert.Equal(serial.TotalMissingInB, parallel.TotalMissingInB);
        Assert.Equal(serial.TotalDiffValues, parallel.TotalDiffValues);
        Assert.Equal(4, parallel.TotalA);
        Assert.Equal(4, parallel.TotalB);

        // T005 beda status: harus tercatat sebagai beda nilai di kedua jalur.
        var serT005 = serial.Rows.Single(r => r.Key == "T005");
        var parT005 = parallel.Rows.Single(r => r.Key == "T005");
        Assert.Equal(RowStatus.Match, parT005.Status);
        Assert.Contains("Status", parT005.DifferingColumns);
        Assert.Equal(serT005.DifferingColumns.OrderBy(c => c), parT005.DifferingColumns.OrderBy(c => c));
    }

    [Fact]
    public void Compare_ParallelLoad_SortedMerge_MatchesSerialResults()
    {
        var pathA = WriteTempCsv(
            "BillingNo,Amount,Status\nT001,100.00,Paid\nT001,100.00,Paid\nT002,250.50,Unpaid\nT003,30.00,Paid\n");
        var pathB = WriteTempCsv(
            "BillingNo,Amount,Status\nT001,100.00,Paid\nT002,260.00,Paid\nT004,10.00,Paid\n");

        var a = Csv("A", pathA, ["BillingNo"], ["Amount", "Status"]);
        var b = Csv("B", pathB, ["BillingNo"], ["Amount", "Status"]);

        var serial = new CompareEngine(a, b, numericTolerance: 0m) { AssumeSortedByKey = true }.Compare();
        var parallel = new CompareEngine(a, b, numericTolerance: 0m) { AssumeSortedByKey = true, ParallelLoad = 4 }.Compare();

        Assert.Equal(serial.TotalA, parallel.TotalA);
        Assert.Equal(serial.TotalB, parallel.TotalB);
        Assert.Equal(serial.TotalMatched, parallel.TotalMatched);
        Assert.Equal(serial.TotalMissingInA, parallel.TotalMissingInA);
        Assert.Equal(serial.TotalMissingInB, parallel.TotalMissingInB);
        Assert.Equal(serial.TotalDiffValues, parallel.TotalDiffValues);

        var t002 = parallel.Rows.Single(r => r.Key == "T002");
        Assert.Equal(["Amount", "Status"], t002.DifferingColumns);
    }

    [Fact]
    public void SqlSegmenter_BuildSegmentDefinitions_ProducesNonOverlappingRangeQueries()
    {
        var def = new DataSourceDefinition
        {
            Name = "A",
            Kind = "sql",
            Query = "SELECT BillingNo, Amount FROM dbo.Transactions ORDER BY BillingNo",
            KeyColumns = ["BillingNo"],
            CompareColumns = ["Amount"]
        };

        var analysis = new SqlSegmentAnalysis("BillingNo", 1000, 3, ["T100", "T700"]);
        var segments = SqlSegmenter.BuildSegmentDefinitions(def, analysis, numericKey: false);

        Assert.Equal(3, segments.Count);

        // Segmen 1: hanya <= T100
        Assert.Contains("_s WHERE [BillingNo] <= 'T100'", segments[0].Query);
        // Segmen 2: > T100 AND <= T700
        Assert.Contains("[BillingNo] > 'T100' AND [BillingNo] <= 'T700'", segments[1].Query);
        // Segmen 3: > T700
        Assert.Contains("[BillingNo] > 'T700'", segments[2].Query);

        // Nama difiks akhiran segmen
        Assert.Equal("A[1/3]", segments[0].Name);
        Assert.Equal("A[3/3]", segments[2].Name);

        // KeyColumns & CompareColumns tetap diwarisi
        Assert.Equal("BillingNo", segments[0].KeyColumns.Single());
        Assert.Equal("Amount", segments[1].CompareColumns.Single());
    }

    [Fact]
    public void SqlSegmenter_BuildSegmentDefinitions_NumericKey_QuotesWithNoQuote()
    {
        var def = new DataSourceDefinition
        {
            Name = "A",
            Kind = "sql",
            Query = "SELECT id, v FROM dbo.T",
            KeyColumns = ["id"],
        };

        var analysis = new SqlSegmentAnalysis("id", 500, 2, ["250"]);
        var segments = SqlSegmenter.BuildSegmentDefinitions(def, analysis, numericKey: true);

        Assert.Contains("_s WHERE [id] <= 250", segments[0].Query);
        Assert.Contains("[id] > 250", segments[1].Query);
    }

    [Fact]
    public void SqlSource_ResolveConnectionString_UsesSqlAuthAndRemovesTrustedConnection()
    {
        var def = new DataSourceDefinition
        {
            Name = "A",
            Kind = "sql",
            Connection = "Server=oldhost;Database=OldDb;Trusted_Connection=True;",
            Server = "newhost,1433",
            Database = "NewDb",
            Username = "sql_user",
            Password = "secret_password"
        };

        var connStr = SqlSource.ResolveConnectionString(def);
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connStr);

        Assert.Equal("newhost,1433", builder.DataSource);
        Assert.Equal("NewDb", builder.InitialCatalog);
        Assert.False(builder.IntegratedSecurity);
        Assert.Equal("sql_user", builder.UserID);
        Assert.Equal("secret_password", builder.Password);
    }
}
