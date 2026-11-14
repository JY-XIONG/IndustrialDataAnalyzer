using System.Windows.Controls;

namespace IndustrialDataAnalyzer.App.Controls;

/// <summary>Provides the shared read-only table appearance.</summary>
public sealed class DataTable : DataGrid
{
    /// <summary>Creates a read-only table intended for later data binding.</summary>
    public DataTable()
    {
        IsReadOnly = true;
        AutoGenerateColumns = false;
        CanUserAddRows = false;
        CanUserDeleteRows = false;
    }
}
