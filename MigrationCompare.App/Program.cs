using MigrationCompare.Core.Compare;
using MigrationCompare.Core.Models;
using MigrationCompare.Core.Quality;
using MigrationCompare.Core.Report;
using MigrationCompare.Core.Sources;

namespace MigrationCompare.App;

/// <summary>Battle: aplikasi konsole komparasi + kualitas data (read-only).</summary>
public static class Program
{
    private const string ReportDir = "laporan";

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length > 0 && (args[0] == "--compare" || args[0] == "-c"))
            return await RunCompareCliAsync(args);

        if (args.Length > 0 && (args[0] == "--quality" || args[0] == "-q"))
            return await RunQualityCliAsync(args);

        return await RunMenuAsync();
    }

    // ---------- Mode Menu interaktif ----------

    private static async Task<int> RunMenuAsync()
    {
        while (true)
        {
            Console.Clear();
            Console.WriteLine("=== MigrationCompare ===");
            Console.WriteLine("Tools komparasi & kualitas data (read-only)");
            Console.WriteLine();
            Console.WriteLine("1) Komparasi dua sumber data (bandingkan value + selisih baris)");
            Console.WriteLine("2) Pemeriksaan kualitas satu sumber (null / kosong / duplikat)");
            Console.WriteLine("3) Keluar");
            Console.WriteLine();

            var choice = Ask("Pilih menu [1-3]: ");
            switch (choice)
            {
                case "1": await RunCompareInteractiveAsync(); break;
                case "2": await RunQualityInteractiveAsync(); break;
                case "3": return 0;
            }
        }
    }

    private static async Task RunCompareInteractiveAsync()
    {
        try
        {
            var a = AskSource("Sumber A");
            var b = AskSource("Sumber B");
            var tol = ParseDecimal(AskOrDefault("Toleransi numerik (0 = harus sama persis): ", "0"));

            var result = new CompareEngine(a, b, tol).Compare();
            PrintCompareSummary(result);
            await PromptSaveReportsAsync("laporan-komparasi", result, null, a.Name, b.Name);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nError: {ex.Message}");
        }
        Console.WriteLine("\nTekan Enter untuk kembali.");
        Console.ReadLine();
    }

    private static async Task RunQualityInteractiveAsync()
    {
        try
        {
            var def = AskSource("Sumber data");
            var report = new QualityChecker(def).Check();
            PrintQualitySummary(report);
            await PromptSaveReportsAsync("laporan-kualitas", null, report, null, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nError: {ex.Message}");
        }
        Console.WriteLine("\nTekan Enter untuk kembali.");
        Console.ReadLine();
    }

    // ---------- Mode CLI (scriptable) ----------

    private static async Task<int> RunCompareCliAsync(string[] args)
    {
        try
        {
            var (a, b) = ParseCompareArgs(args);
            var tol = GetArgDecimal(args, "--tol", 0m);

            Console.WriteLine("Membaca & membandingkan...");
            var result = new CompareEngine(a, b, tol).Compare();
            PrintCompareSummary(result);

            var outBase = GetArg(args, "--out");
            if (outBase is not null)
            {
                Directory.CreateDirectory(ReportDir);
                var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                var basePath = Path.Combine(ReportDir, $"{outBase}-{stamp}");
                var (mdPath, csvPath) = UniquePaths(basePath, "keduanya");
                await File.WriteAllTextAsync(mdPath, MarkdownReport.Compare(result, a.Name, b.Name));
                await File.WriteAllTextAsync(csvPath, CsvReport.Compare(result));
                Console.WriteLine($"Laporan disimpan: {Path.GetFullPath(mdPath)} / {Path.GetFileName(csvPath)}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunQualityCliAsync(string[] args)
    {
        try
        {
            var def = ParseQualityArgs(args);
            Console.WriteLine("Memeriksa kualitas...");
            var report = new QualityChecker(def).Check();
            PrintQualitySummary(report);

            var outBase = GetArg(args, "--out");
            if (outBase is not null)
            {
                Directory.CreateDirectory(ReportDir);
                var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                var basePath = Path.Combine(ReportDir, $"{outBase}-{stamp}");
                var (mdPath, csvPath) = UniquePaths(basePath, "keduanya");
                await File.WriteAllTextAsync(mdPath, MarkdownReport.Quality(report));
                await File.WriteAllTextAsync(csvPath, CsvReport.Quality(report));
                Console.WriteLine($"Laporan disimpan: {Path.GetFullPath(mdPath)} / {Path.GetFileName(csvPath)}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    // ---------- Parser CLI ----------

    private static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name) return args[i + 1];
        }
        return null;
    }

    private static decimal GetArgDecimal(string[] args, string name, decimal def) =>
        GetArg(args, name) is { } s && decimal.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : def;

    private static DataSourceDefinition ParseSource(string label, string kind, string conn, string query, string keys, string cols) =>
        new()
        {
            Name = label,
            Kind = kind,
            Connection = conn,
            Query = query,
            KeyColumns = keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            CompareColumns = cols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
        };

    private static (DataSourceDefinition, DataSourceDefinition) ParseCompareArgs(string[] args)
    {
        var kindA = GetArg(args, "--a-kind") ?? "sql";
        var kindB = GetArg(args, "--b-kind") ?? "sql";
        var a = ParseSource("Sumber A", kindA,
            GetArg(args, "--a") ?? throw new ArgumentException("Wajib: --a <connection|path>"),
            GetArg(args, "--a-query") ?? "",
            GetArg(args, "--a-keys") ?? throw new ArgumentException("Wajib: --a-keys"),
            GetArg(args, "--a-cols") ?? "");
        var b = ParseSource("Sumber B", kindB,
            GetArg(args, "--b") ?? throw new ArgumentException("Wajib: --b <connection|path>"),
            GetArg(args, "--b-query") ?? "",
            GetArg(args, "--b-keys") ?? throw new ArgumentException("Wajib: --b-keys"),
            GetArg(args, "--b-cols") ?? "");
        return (a, b);
    }

    private static DataSourceDefinition ParseQualityArgs(string[] args)
    {
        var kind = GetArg(args, "--kind") ?? "sql";
        return ParseSource("Sumber data", kind,
            GetArg(args, "--src") ?? throw new ArgumentException("Wajib: --src <connection|path>"),
            GetArg(args, "--query") ?? "",
            GetArg(args, "--keys") ?? throw new ArgumentException("Wajib: --keys"),
            GetArg(args, "--cols") ?? "");
    }

    // ---------- Print summary ----------

    private static void PrintCompareSummary(CompareResult result)
    {
        Console.WriteLine("\n=== Ringkasan ===");
        Console.WriteLine($"Total baris A          : {result.TotalA}");
        Console.WriteLine($"Total baris B          : {result.TotalB}");
        Console.WriteLine($"Cocok (matched)        : {result.TotalMatched}");
        Console.WriteLine($"Ada di A, tidak di B   : {result.TotalMissingInB}");
        Console.WriteLine($"Ada di B, tidak di A   : {result.TotalMissingInA}");
        Console.WriteLine($"Cocok tapi nilai beda  : {result.TotalDiffValues}");
    }

    private static void PrintQualitySummary(QualityReport report)
    {
        Console.WriteLine("\n=== Ringkasan Kualitas ===");
        Console.WriteLine($"Total baris   : {report.TotalRows}");
        Console.WriteLine($"Total sel NULL : {report.NullCells}");
        Console.WriteLine($"Key duplikat  : {report.DuplicateKeys}");
        foreach (var c in report.Columns)
        {
            var n = report.NullPerColumn.TryGetValue(c, out var nv) ? nv : 0;
            var e = report.EmptyPerColumn.TryGetValue(c, out var ev) ? ev : 0;
            if (n > 0 || e > 0) Console.WriteLine($"  {c}: NULL={n}, kosong={e}");
        }
    }

    // ---------- Save reports ----------

    private static async Task PromptSaveReportsAsync(string baseName, CompareResult? cmp, QualityReport? qlt, string? nameA, string? nameB)
    {
        var fmt = Ask("Simpan laporan? [md/csv/keduanya/tidak]: ").ToLowerInvariant();
        if (fmt == "tidak" || fmt == "t" || fmt == "n") return;

        Directory.CreateDirectory(ReportDir);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var basePath = Path.Combine(ReportDir, $"{baseName}-{stamp}");

        var md = cmp is not null ? MarkdownReport.Compare(cmp, nameA!, nameB!) : MarkdownReport.Quality(qlt!);
        var csv = cmp is not null ? CsvReport.Compare(cmp) : CsvReport.Quality(qlt!);

        // Pastikan nama unik: kalau sudah ada, tambah suffix -2, -3, dst.
        var (mdPath, csvPath) = UniquePaths(basePath, fmt);
        if (fmt == "md" || fmt == "keduanya")
        {
            await File.WriteAllTextAsync(mdPath, md);
            Console.WriteLine($"Laporan MD  : {Path.GetFullPath(mdPath)}");
        }
        if (fmt == "csv" || fmt == "keduanya")
        {
            await File.WriteAllTextAsync(csvPath, csv);
            Console.WriteLine($"Laporan CSV : {Path.GetFullPath(csvPath)}");
        }
    }

    private static (string mdPath, string csvPath) UniquePaths(string basePath, string fmt)
    {
        int n = 0;
        while (true)
        {
            var suffix = n == 0 ? "" : $"-{n}";
            var mdPath = basePath + suffix + ".md";
            var csvPath = basePath + suffix + ".csv";

            bool mdExists = (fmt == "md" || fmt == "keduanya") && File.Exists(mdPath);
            bool csvExists = (fmt == "csv" || fmt == "keduanya") && File.Exists(csvPath);
            if (!mdExists && !csvExists) return (mdPath, csvPath);
            n++;
        }
    }

    // ---------- Helpers input ----------

    private static string Ask(string prompt, string? defaultValue = null)
    {
        while (true)
        {
            Console.Write(prompt);
            var line = Console.ReadLine()?.Trim() ?? "";
            if (line.Length > 0) return line;
            if (defaultValue is not null) return defaultValue;
        }
    }

    private static string AskOrDefault(string prompt, string defaultValue)
    {
        Console.Write(prompt);
        var line = Console.ReadLine()?.Trim() ?? "";
        return line.Length > 0 ? line : defaultValue;
    }

    private static decimal ParseDecimal(string s) =>
        decimal.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0m;

    private static DataSourceDefinition AskSource(string label)
    {
        Console.WriteLine($"\n--- Konfigurasi {label} ---");
        var kind = Ask("Jenis sumber [sql/csv]: ").ToLowerInvariant();
        return new DataSourceDefinition
        {
            Name = label,
            Kind = kind,
            Connection = Ask("Connection string (sql) / path file (csv): "),
            Query = kind == "sql" ? Ask("Query SELECT: ") : "",
            KeyColumns = Ask("Kolom kunci (pisah koma, mis. BillingNo): ")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
            CompareColumns = AskOrDefault("Kolom yang dibandingkan (kosong = semua non-key): ", "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList()
        };
    }
}
