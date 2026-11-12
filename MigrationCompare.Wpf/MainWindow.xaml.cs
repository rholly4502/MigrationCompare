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

    public MainWindow()
    {
        InitializeComponent();
        KindA.SelectionChanged += (_, _) => ToggleQueryFields();
        KindB.SelectionChanged += (_, _) => ToggleQueryFields();
        ToggleQueryFields();
        StatusText.Text = "Siap. Isi konfigurasi sumber, lalu klik 'Bandingkan'.";
    }

    private void ToggleQueryFields()
    {
        QueryA.IsEnabled = KindA.SelectedIndex == 0;
        QueryB.IsEnabled = KindB.SelectedIndex == 0;
    }

    private DataSourceDefinition GetSourceDefinition(string label, ComboBox kind,
        TextBox conn, TextBox query, TextBox keys)
    {
        var isSql = kind.SelectedIndex == 0;
        var keysList = keys.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (keysList.Count == 0)
            throw new InvalidOperationException($"'{label}': kolom kunci kosong.");

        if (isSql && string.IsNullOrWhiteSpace(query.Text))
            throw new InvalidOperationException($"'{label}': query SELECT wajib diisi untuk SQL.");

        return new DataSourceDefinition
        {
            Name = label,
            Kind = isSql ? "sql" : "csv",
            Connection = conn.Text.Trim(),
            Query = isSql ? query.Text.Trim() : "",
            KeyColumns = keysList
        };
    }

    private void CompareBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var a = GetSourceDefinition("Sumber A", KindA, ConnectionA, QueryA, KeysA);
            var b = GetSourceDefinition("Sumber B", KindB, ConnectionB, QueryB, KeysB);

            var tol = 0m;
            if (ToleranceBox.IsChecked == true)
            {
                if (!decimal.TryParse(ToleranceInput.Text, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out tol))
                {
                    MessageBox.Show("Toleransi numerik tidak valid.", "Input tidak valid",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            StatusText.Text = "Membaca & membandingkan...";
            var engine = new CompareEngine(a, b, tol);
            _compareResult = engine.Compare();
            _qualityReport = null;

            SummaryText.Text =
                $"A={_compareResult.TotalA} · B={_compareResult.TotalB} · " +
                $"cocok={_compareResult.TotalMatched} · " +
                $"hanya A={_compareResult.TotalMissingInB} · " +
                $"hanya B={_compareResult.TotalMissingInA} · " +
                $"beda={_compareResult.TotalDiffValues}";

            // Tampilkan baris yang tidak identical (missing + diff) supaya berguna.
            ResultGrid.ItemsSource = _compareResult.Rows
                .Where(r => r.Status != RowStatus.Match || r.DifferingColumns.Count > 0)
                .Select(r => new
                {
                    Key = r.Key,
                    Status = r.Status switch
                    {
                        RowStatus.Match => "cocok (beda nilai)",
                        RowStatus.MissingInB => "hanya di A",
                        RowStatus.MissingInA => "hanya di B",
                        _ => ""
                    },
                    KolomBeda = r.Status == RowStatus.Match ? string.Join(", ", r.DifferingColumns) : "",
                    NilaiA = r.RowA?.Values.OrderBy(kv => kv.Key).FirstOrDefault().Value ?? "",
                    NilaiB = r.RowB?.Values.OrderBy(kv => kv.Key).FirstOrDefault().Value ?? ""
                })
                .ToList();

            GridCaption.Text = $"Menampilkan {_compareResult.Rows.Count(r => r.Status != RowStatus.Match || r.DifferingColumns.Count > 0)} baris berbeda/tidak cocok dari {_compareResult.Rows.Count} total pasangan kunci.";
            SaveBtn.IsEnabled = true;
            StatusText.Text = "Selesai.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Gagal.";
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
                FileName = "laporan-komparasi"
            };
            if (dlg.ShowDialog() != true) return;

            var path = dlg.FileName;
            var isMd = path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
            var isCsv = path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

            string content;
            if (_qualityReport is not null)
            {
                content = isCsv ? CsvReport.Quality(_qualityReport) : MarkdownReport.Quality(_qualityReport);
            }
            else
            {
                string? nameA = "Sumber A", nameB = "Sumber B";
                content = isCsv
                    ? CsvReport.Compare(_compareResult!)
                    : MarkdownReport.Compare(_compareResult!, nameA, nameB);
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
