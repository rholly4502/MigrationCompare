using MigrationCompare.Core.Models;

namespace MigrationCompare.Core.Sources;

/// <summary>Abstraksi sumber data (SQL/CSV) yang bisa dibaca baris per baris.</summary>
public interface IDataSource : IDisposable
{
    string Name { get; }

    /// <summary>Membuka sumber dan mengembalikan daftar kolom yang tersedia.</summary>
    IReadOnlyList<string> Open();

    /// <summary>Membaca baris berikutnya; null saat habis.</summary>
    DataRow? ReadRow();
}
