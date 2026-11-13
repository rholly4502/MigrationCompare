using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MigrationCompare.Core.Compare;
using MigrationCompare.Core.Models;
using MigrationCompare.Core.Quality;
using MigrationCompare.Core.Report;
using MigrationCompare.Core.Sources;

namespace MigrationCompare.Wpf;

public partial class MainWindow : Window
{
    private CompareResult? _compareResult;
    private QualityReport? _qualityReport;
    private CancellationTokenSource? _cts;
    private List<CompareGridRow> _allDiffGridRows = new();

    private sealed class CompareGridRow
    {
        public string Key { get; init; } = "";
        public RowStatus RawStatus { get; init; }
        public string Status { get; init; } = "";
        public string KolomBeda { get; init; } = "";
        public string NilaiA { get; init; } = "";
        public string NilaiB { get; init; } = "";
    }

    private sealed class QualityGridRow
    {
        public string Kolom { get; init; } = "";
        public long NullCount { get; init; }
        public long EmptyCount { get; init; }
    }

    public MainWindow()
    {
        InitializeComponent();
        KindA.SelectionChanged += (_, _) => ToggleSourceFields();
        KindB.SelectionChanged += (_, _) => ToggleSourceFields();
        ToggleSourceFields();
        ToggleAuthFields(AuthModeA, UserA, PassA);
        ToggleAuthFields(AuthModeB, UserB, PassB);
        StatusText.Text = "Siap. Isi Server, Database, Username & Password (atau pilih CSV), lalu klik 'Test Koneksi' atau 'Bandingkan'.";
    }

    private void ToggleSourceFields()
    {
        bool sqlA = KindA.SelectedIndex == 0;
        SqlAuthSectionA.Visibility = sqlA ? Visibility.Visible : Visibility.Collapsed;
        QuerySectionA.Visibility = sqlA ? Visibility.Visible : Visibility.Collapsed;
        BrowseA.Visibility = sqlA ? Visibility.Collapsed : Visibility.Visible;
        ConnLabelA.Text = sqlA ? "Connection String Tambahan / Override (opsional):" : "Lokasi File CSV:";
        ConnHintA.Text = sqlA
            ? "Opsional jika Server, Database, Username & Password di atas sudah diisi (contoh: Encrypt=False;)."
            : "Contoh: C:\\Data\\transaksi_lama.csv (baris pertama harus berisi nama kolom/header)";

        bool sqlB = KindB.SelectedIndex == 0;
        SqlAuthSectionB.Visibility = sqlB ? Visibility.Visible : Visibility.Collapsed;
        QuerySectionB.Visibility = sqlB ? Visibility.Visible : Visibility.Collapsed;
        BrowseB.Visibility = sqlB ? Visibility.Collapsed : Visibility.Visible;
        ConnLabelB.Text = sqlB ? "Connection String Tambahan / Override (opsional):" : "Lokasi File CSV:";
        ConnHintB.Text = sqlB
            ? "Opsional jika Server, Database, Username & Password di atas sudah diisi (contoh: Encrypt=False;)."
            : "Contoh: C:\\Data\\transaksi_baru.csv (baris pertama harus berisi nama kolom/header)";
    }

    private void AuthModeA_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (UserA is not null && PassA is not null)
            ToggleAuthFields(AuthModeA, UserA, PassA);
    }

    private void AuthModeB_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (UserB is not null && PassB is not null)
            ToggleAuthFields(AuthModeB, UserB, PassB);
    }

    private static void ToggleAuthFields(ComboBox authMode, TextBox userBox, PasswordBox passBox)
    {
        bool sqlLogin = authMode.SelectedIndex == 0;
        userBox.IsEnabled = sqlLogin;
        passBox.IsEnabled = sqlLogin;
    }

    private void ToleranceBox_Changed(object sender, RoutedEventArgs e)
    {
        if (ToleranceInput is not null)
            ToleranceInput.IsEnabled = ToleranceBox.IsChecked == true;
    }

    private void BrowseA_Click(object sender, RoutedEventArgs e) => BrowseCsvFile(ConnectionA);
    private void BrowseB_Click(object sender, RoutedEventArgs e) => BrowseCsvFile(ConnectionB);

    private static void BrowseCsvFile(TextBox target)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pilih File CSV",
            Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            target.Text = dlg.FileName;
        }
    }

    private void FillSampleA_Click(object sender, RoutedEventArgs e) =>
        FillSample(KindA, ServerA, DatabaseA, AuthModeA, UserA, PassA, ConnectionA, QueryA, KeysA, isSourceA: true);

    private void FillSampleB_Click(object sender, RoutedEventArgs e) =>
        FillSample(KindB, ServerB, DatabaseB, AuthModeB, UserB, PassB, ConnectionB, QueryB, KeysB, isSourceA: false);

    private void FillSample(
        ComboBox kind,
        TextBox server,
        TextBox database,
        ComboBox authMode,
        TextBox user,
        PasswordBox pass,
        TextBox conn,
        TextBox query,
        TextBox keys,
        bool isSourceA)
    {
        if (kind.SelectedIndex == 0)
        {
            server.Text = "localhost,1433";
            database.Text = isSourceA ? "LegacyDb" : "NewDb";
            authMode.SelectedIndex = 0; // SQL Login (User & Password)
            user.Text = "sa";
            pass.Password = "";
            conn.Text = "";
            query.Text = isSourceA
                ? "SELECT BillingNo, Amount, Status FROM dbo.Transactions ORDER BY BillingNo"
                : "SELECT BillingNo, Amount, Status FROM dbo.Transactions_Migrated ORDER BY BillingNo";
        }
        else
        {
            conn.Text = isSourceA ? @"C:\Data\source_a.csv" : @"C:\Data\source_b.csv";
        }

        keys.Text = "BillingNo";
        if (string.IsNullOrWhiteSpace(CompareColsInput.Text))
            CompareColsInput.Text = "Amount, Status";
    }

    private DataSourceDefinition GetSourceDefinition(
        string label,
        ComboBox kind,
        TextBox server,
        TextBox database,
        ComboBox authMode,
        TextBox user,
        PasswordBox pass,
        TextBox conn,
        TextBox query,
        TextBox keys,
        IReadOnlyList<string>? fallbackKeys = null,
        bool includeCompareCols = false,
        bool requireQueryAndKeys = true)
    {
        var isSql = kind.SelectedIndex == 0;
        var connStr = conn.Text.Trim();
        var serverStr = server.Text.Trim();
        var dbStr = database.Text.Trim();
        var useWindowsAuth = authMode.SelectedIndex == 1;
        var userStr = useWindowsAuth ? "" : user.Text.Trim();
        var passStr = useWindowsAuth ? "" : pass.Password;

        if (isSql)
        {
            if (string.IsNullOrWhiteSpace(serverStr) && string.IsNullOrWhiteSpace(connStr))
                throw new InvalidOperationException($"'{label}': Server/Host atau Connection String SQL wajib diisi.");

            if (!useWindowsAuth && string.IsNullOrWhiteSpace(userStr) && string.IsNullOrWhiteSpace(connStr))
                throw new InvalidOperationException($"'{label}': Username SQL wajib diisi untuk SQL Login (atau pilih Windows Authentication).");

            if (requireQueryAndKeys && string.IsNullOrWhiteSpace(query.Text))
                throw new InvalidOperationException($"'{label}': query SELECT wajib diisi untuk SQL.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(connStr))
                throw new InvalidOperationException($"'{label}': lokasi file CSV wajib diisi.");
        }

        var keysList = keys.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (keysList.Count == 0 && fallbackKeys is { Count: > 0 })
            keysList = fallbackKeys.ToList();

        if (requireQueryAndKeys && keysList.Count == 0)
            throw new InvalidOperationException($"'{label}': kolom kunci wajib diisi.");

        var compareCols = includeCompareCols
            ? CompareColsInput.Text
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList()
            : new List<string>();

        var def = new DataSourceDefinition
        {
            Name = label,
            Kind = isSql ? "sql" : "csv",
            Connection = connStr,
            Server = serverStr,
            Database = dbStr,
            Username = userStr,
            Password = passStr,
            UseWindowsAuth = useWindowsAuth,
            Query = isSql ? query.Text.Trim() : "",
            KeyColumns = keysList,
            CompareColumns = compareCols
        };

        if (isSql)
        {
            def.Connection = SqlSource.ResolveConnectionString(def);
        }

        return def;
    }

    private void SetBusy(bool isBusy)
    {
        CompareBtn.IsEnabled = !isBusy;
        QualityABtn.IsEnabled = !isBusy;
        QualityBBtn.IsEnabled = !isBusy;
        TestConnA.IsEnabled = !isBusy;
        TestConnB.IsEnabled = !isBusy;
        CancelBtn.IsEnabled = isBusy;
        BusyBar.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        if (isBusy) SaveBtn.IsEnabled = false;
    }

    private async void TestConnA_Click(object sender, RoutedEventArgs e) =>
        await RunTestConnectionAsync("Sumber A", KindA, ServerA, DatabaseA, AuthModeA, UserA, PassA, ConnectionA, QueryA, KeysA);

    private async void TestConnB_Click(object sender, RoutedEventArgs e) =>
        await RunTestConnectionAsync("Sumber B", KindB, ServerB, DatabaseB, AuthModeB, UserB, PassB, ConnectionB, QueryB, KeysB);

    private async Task RunTestConnectionAsync(
        string label,
        ComboBox kind,
        TextBox server,
        TextBox database,
        ComboBox authMode,
        TextBox user,
        PasswordBox pass,
        TextBox conn,
        TextBox query,
        TextBox keys)
    {
        try
        {
            var def = GetSourceDefinition(label, kind, server, database, authMode, user, pass, conn, query, keys,
                requireQueryAndKeys: false);

            _cts = new CancellationTokenSource();
            SetBusy(true);
            StatusText.Text = $"Menguji koneksi {label}...";

            if (def.Kind == "sql")
            {
                var (srv, db, cols) = await SqlSource.TestConnectionAsync(def, timeoutSeconds: 10, _cts.Token);
                var colInfo = cols.Count > 0
                    ? $"\n\nQuery valid! Ditemukan {cols.Count} kolom:\n{string.Join(", ", cols)}"
                    : "\n\n(Query masih kosong — koneksi server/database berhasil diverifikasi.)";

                StatusText.Text = $"Test koneksi {label} berhasil ({srv} / {db}).";
                MessageBox.Show(
                    $"Koneksi {label} BERHASIL!\n\nServer: {srv}\nDatabase: {db}{colInfo}",
                    "Test Koneksi Berhasil",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                var cols = await Task.Run(() =>
                {
                    using var csv = new CsvSource(def);
                    return csv.Open();
                }, _cts.Token);

                StatusText.Text = $"Test file CSV {label} berhasil ({cols.Count} kolom).";
                MessageBox.Show(
                    $"File CSV {label} valid!\n\nPath: {def.Connection}\nDitemukan {cols.Count} kolom:\n{string.Join(", ", cols)}",
                    "Test CSV Berhasil",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = $"Test koneksi {label} dibatalkan.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Test koneksi {label} gagal.";
            MessageBox.Show($"Test koneksi {label} GAGAL:\n\n{ex.Message}",
                "Test Koneksi Gagal", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async void CompareBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var a = GetSourceDefinition("Sumber A", KindA, ServerA, DatabaseA, AuthModeA, UserA, PassA, ConnectionA, QueryA, KeysA,
                includeCompareCols: true);
            var b = GetSourceDefinition("Sumber B", KindB, ServerB, DatabaseB, AuthModeB, UserB, PassB, ConnectionB, QueryB, KeysB,
                fallbackKeys: a.KeyColumns);

            var tol = 0m;
            if (ToleranceBox.IsChecked == true)
            {
                if (!decimal.TryParse(ToleranceInput.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out tol) || tol < 0)
                {
                    MessageBox.Show("Toleransi numerik harus berupa angka >= 0 (gunakan titik untuk desimal, mis. 0.01).",
                        "Input tidak valid", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            var assumeSorted = StreamingModeBox.IsChecked == true;

            _cts = new CancellationTokenSource();
            SetBusy(true);
            StatusText.Text = assumeSorted
                ? "Memproses (Mode Streaming Hemat RAM)..."
                : "Membaca & membandingkan data...";

            var progress = new Progress<(long rowsA, long rowsB)>(p =>
            {
                StatusText.Text = $"Memproses... Sumber A: {p.rowsA:N0} baris | Sumber B: {p.rowsB:N0} baris";
            });

            var engine = new CompareEngine(a, b, tol)
            {
                AssumeSortedByKey = assumeSorted
            };

            var result = await Task.Run(() => engine.Compare(progress, _cts.Token), _cts.Token);

            _compareResult = result;
            _qualityReport = null;

            UpdateCompareMetrics(result);

            _allDiffGridRows = result.Rows
                .Where(r => r.Status != RowStatus.Match || r.DifferingColumns.Count > 0)
                .Select(r => new CompareGridRow
                {
                    Key = r.Key,
                    RawStatus = r.Status,
                    Status = r.Status switch
                    {
                        RowStatus.Match => "cocok (beda nilai)",
                        RowStatus.MissingInB => "hanya di A",
                        RowStatus.MissingInA => "hanya di B",
                        _ => ""
                    },
                    KolomBeda = r.Status == RowStatus.Match ? string.Join(", ", r.DifferingColumns) : "",
                    NilaiA = FormatRowValues(r, isSourceA: true),
                    NilaiB = FormatRowValues(r, isSourceA: false)
                })
                .ToList();

            StatusFilter.IsEnabled = true;
            SearchKeyBox.IsEnabled = true;
            StatusFilter.SelectedIndex = 0;
            ApplyCompareFilter();

            SummaryText.Text = result.IsTruncated
                ? $"Selesai (tabel dibatasi {result.Rows.Count:N0} baris di RAM; metrik tetap akurat)."
                : $"Kolom dibandingkan: {string.Join(", ", result.CompareColumns)}";
            SaveBtn.IsEnabled = true;
            StatusText.Text = $"Selesai. Total A={result.TotalA:N0}, B={result.TotalB:N0}, Beda={result.TotalDiffValues:N0}, Hanya A={result.TotalMissingInB:N0}, Hanya B={result.TotalMissingInA:N0}.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Proses dibatalkan oleh pengguna.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error Komparasi", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Gagal.";
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async void QualityABtn_Click(object sender, RoutedEventArgs e) =>
        await RunQualityCheckAsync("Sumber A", KindA, ServerA, DatabaseA, AuthModeA, UserA, PassA, ConnectionA, QueryA, KeysA);

    private async void QualityBBtn_Click(object sender, RoutedEventArgs e) =>
        await RunQualityCheckAsync("Sumber B", KindB, ServerB, DatabaseB, AuthModeB, UserB, PassB, ConnectionB, QueryB, KeysB);

    private async Task RunQualityCheckAsync(
        string label,
        ComboBox kind,
        TextBox server,
        TextBox database,
        ComboBox authMode,
        TextBox user,
        PasswordBox pass,
        TextBox conn,
        TextBox query,
        TextBox keys)
    {
        try
        {
            var def = GetSourceDefinition(label, kind, server, database, authMode, user, pass, conn, query, keys);

            _cts = new CancellationTokenSource();
            SetBusy(true);
            StatusText.Text = $"Memeriksa kualitas {label}...";

            var progress = new Progress<long>(rows =>
            {
                StatusText.Text = $"Memeriksa kualitas {label}... {rows:N0} baris";
            });

            var checker = new QualityChecker(def);
            var report = await Task.Run(() => checker.Check(progress, _cts.Token), _cts.Token);

            _qualityReport = report;
            _compareResult = null;
            _allDiffGridRows.Clear();

            StatusFilter.IsEnabled = false;
            SearchKeyBox.IsEnabled = false;

            MetricTotalA.Text = label == "Sumber A" ? $"{report.TotalRows:N0}" : "-";
            MetricTotalB.Text = label == "Sumber B" ? $"{report.TotalRows:N0}" : "-";
            MetricMatched.Text = $"{report.Columns.Count:N0} kol";
            MetricDiff.Text = $"{report.NullCells:N0} null";
            MetricOnlyA.Text = $"{report.DuplicateKeys:N0} dup";
            MetricOnlyB.Text = $"{report.EmptyPerColumn.Values.Sum():N0} ksg";

            var qualityRows = report.Columns.Select(c => new QualityGridRow
            {
                Kolom = c,
                NullCount = report.NullPerColumn.TryGetValue(c, out var nv) ? nv : 0,
                EmptyCount = report.EmptyPerColumn.TryGetValue(c, out var ev) ? ev : 0
            }).ToList();

            ResultGrid.ItemsSource = qualityRows;
            SummaryText.Text = $"Laporan Kualitas {label}: {report.TotalRows:N0} baris, {report.NullCells:N0} sel NULL, {report.DuplicateKeys:N0} key duplikat.";
            GridCaption.Text = $"Menampilkan rincian NULL & string kosong untuk {report.Columns.Count} kolom pada {label}.";
            SaveBtn.IsEnabled = true;
            StatusText.Text = $"Pemeriksaan kualitas {label} selesai.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Pemeriksaan kualitas dibatalkan.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error Cek Kualitas", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Gagal.";
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelBtn.IsEnabled = false;
        StatusText.Text = "Membatalkan proses...";
    }

    private void UpdateCompareMetrics(CompareResult result)
    {
        MetricTotalA.Text = $"{result.TotalA:N0}";
        MetricTotalB.Text = $"{result.TotalB:N0}";
        MetricMatched.Text = $"{result.TotalMatched:N0}";
        MetricDiff.Text = $"{result.TotalDiffValues:N0}";
        MetricOnlyA.Text = $"{result.TotalMissingInB:N0}";
        MetricOnlyB.Text = $"{result.TotalMissingInA:N0}";
    }

    private static string FormatRowValues(CompareResultRow r, bool isSourceA)
    {
        var row = isSourceA ? r.RowA : r.RowB;
        if (row is null) return "";

        if (r.Status == RowStatus.Match && r.DifferingColumns.Count > 0)
        {
            return string.Join("; ", r.DifferingColumns.Select(c => $"{c}={row.Get(c) ?? "<NULL>"}"));
        }

        return string.Join("; ", row.EnumerateValues()
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"{kv.Key}={kv.Value}"));
    }

    private void FilterChanged(object sender, EventArgs e)
    {
        if (_compareResult is not null)
            ApplyCompareFilter();
    }

    private void ApplyCompareFilter()
    {
        IEnumerable<CompareGridRow> filtered = _allDiffGridRows;

        filtered = StatusFilter.SelectedIndex switch
        {
            1 => filtered.Where(r => r.RawStatus == RowStatus.Match),
            2 => filtered.Where(r => r.RawStatus == RowStatus.MissingInB),
            3 => filtered.Where(r => r.RawStatus == RowStatus.MissingInA),
            _ => filtered
        };

        var search = SearchKeyBox.Text.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            filtered = filtered.Where(r => r.Key.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        var list = filtered.ToList();
        ResultGrid.ItemsSource = list;
        GridCaption.Text = $"Menampilkan {list.Count:N0} baris perbedaan/tidak cocok (dari {_allDiffGridRows.Count:N0} baris tersimpan).";
    }

    private void ResultGrid_AutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CompareGridRow.RawStatus):
                e.Cancel = true;
                break;
            case nameof(CompareGridRow.KolomBeda):
                e.Column.Header = "Kolom Berbeda";
                break;
            case nameof(CompareGridRow.NilaiA):
                e.Column.Header = "Nilai Sumber A";
                e.Column.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
                break;
            case nameof(CompareGridRow.NilaiB):
                e.Column.Header = "Nilai Sumber B";
                e.Column.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
                break;
            case nameof(QualityGridRow.NullCount):
                e.Column.Header = "Jumlah NULL";
                break;
            case nameof(QualityGridRow.EmptyCount):
                e.Column.Header = "Jumlah Kosong";
                break;
        }
    }

    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Simpan Laporan",
                Filter = "Markdown (*.md)|*.md|CSV (*.csv)|*.csv",
                FileName = _qualityReport is not null ? "laporan-kualitas" : "laporan-komparasi"
            };
            if (dlg.ShowDialog() != true) return;

            var path = dlg.FileName;
            var isCsv = path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

            string content;
            if (_qualityReport is not null)
            {
                content = isCsv ? CsvReport.Quality(_qualityReport) : MarkdownReport.Quality(_qualityReport);
            }
            else if (_compareResult is not null)
            {
                content = isCsv
                    ? CsvReport.Compare(_compareResult)
                    : MarkdownReport.Compare(_compareResult, "Sumber A", "Sumber B");
            }
            else
            {
                return;
            }

            System.IO.File.WriteAllText(path, content);
            StatusText.Text = $"Disimpan: {path}";
            MessageBox.Show($"Laporan disimpan:\n{path}", "Berhasil",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
