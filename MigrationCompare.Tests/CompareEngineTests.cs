using MigrationCompare.Core.Compare;
using MigrationCompare.Core.Models;
using MigrationCompare.Core.Quality;
using MigrationCompare.Core.Report;

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
}
