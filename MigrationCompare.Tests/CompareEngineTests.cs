using MigrationCompare.Core.Compare;
using MigrationCompare.Core.Models;
using MigrationCompare.Core.Quality;

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
}
