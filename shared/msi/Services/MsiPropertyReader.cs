using Cimian.Msi.Models;
using Microsoft.Extensions.Logging;
using WixToolset.Dtf.WindowsInstaller;

namespace Cimian.Msi.Services;

/// <summary>
/// Reads metadata from MSI databases using DTF.
/// Replaces the previous PowerShell COM interop approach that was fragile and slow.
/// </summary>
public class MsiPropertyReader
{
    private readonly ILogger<MsiPropertyReader> _logger;

    public MsiPropertyReader(ILogger<MsiPropertyReader> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Extract all standard metadata from an MSI file.
    /// For cimipkg-built MSI, also extracts the embedded CIMIAN_BUILD_INFO YAML.
    /// </summary>
    public MsiMetadata ReadMetadata(string msiPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(msiPath);

        if (!File.Exists(msiPath))
            throw new FileNotFoundException("MSI file not found", msiPath);

        var metadata = new MsiMetadata();

        try
        {
            using var db = new Database(msiPath, DatabaseOpenMode.ReadOnly);

            metadata.ProductName = ReadProperty(db, "ProductName") ?? string.Empty;
            metadata.ProductVersion = ReadProperty(db, "ProductVersion") ?? string.Empty;
            metadata.Manufacturer = ReadProperty(db, "Manufacturer") ?? string.Empty;
            metadata.ProductCode = ReadProperty(db, "ProductCode") ?? string.Empty;
            metadata.UpgradeCode = ReadProperty(db, "UpgradeCode") ?? string.Empty;
            metadata.Description = ReadProperty(db, "ARPCOMMENTS") ?? string.Empty;

            // Cimian-specific properties
            metadata.BuildInfoYaml = ReadProperty(db, "CIMIAN_BUILD_INFO");
            metadata.FullVersion = ReadProperty(db, "CIMIAN_FULL_VERSION") ?? metadata.ProductVersion;
            metadata.Identifier = ReadProperty(db, "CIMIAN_IDENTIFIER") ?? string.Empty;

            // Try to determine architecture from Summary Information
            try
            {
                var template = db.SummaryInfo.Template;
                if (!string.IsNullOrEmpty(template))
                {
                    metadata.Architecture = template.Contains("x64", StringComparison.OrdinalIgnoreCase) ? "x64"
                        : template.Contains("Arm64", StringComparison.OrdinalIgnoreCase) ? "arm64"
                        : template.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "x86"
                        : null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Could not read Summary Information template: {Error}", ex.Message);
            }
        }
        catch (InstallerException ex)
        {
            _logger.LogError("Failed to read MSI database {Path}: {Error}", msiPath, ex.Message);
            throw;
        }

        return metadata;
    }

    /// <summary>
    /// Read a single property from the MSI Property table.
    /// </summary>
    public string? ReadProperty(string msiPath, string propertyName)
    {
        using var db = new Database(msiPath, DatabaseOpenMode.ReadOnly);
        return ReadProperty(db, propertyName);
    }

    /// <summary>
    /// Read all properties from the MSI Property table.
    /// </summary>
    public Dictionary<string, string> ReadAllProperties(string msiPath)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var db = new Database(msiPath, DatabaseOpenMode.ReadOnly);

        if (!db.Tables.Contains("Property"))
            return properties;

        using var view = db.OpenView("SELECT `Property`, `Value` FROM `Property`");
        view.Execute();

        foreach (var record in view)
        {
            using (record)
            {
                var name = record.GetString(1);
                var value = record.GetString(2);
                if (!string.IsNullOrEmpty(name))
                    properties[name] = value ?? string.Empty;
            }
        }

        return properties;
    }

    /// <summary>
    /// List all tables in an MSI database.
    /// </summary>
    public IReadOnlyList<string> ListTables(string msiPath)
    {
        using var db = new Database(msiPath, DatabaseOpenMode.ReadOnly);
        return db.Tables.Cast<TableInfo>().Select(t => t.Name).Order().ToList();
    }

    /// <summary>
    /// Read all rows from a specific MSI table.
    /// Returns column names and row data.
    /// </summary>
    public (IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows) ReadTable(
        string msiPath, string tableName)
    {
        using var db = new Database(msiPath, DatabaseOpenMode.ReadOnly);

        if (!db.Tables.Contains(tableName))
            throw new ArgumentException($"Table '{tableName}' not found in MSI database");

        var tableInfo = db.Tables[tableName];
        var columns = tableInfo.Columns.Cast<ColumnInfo>().Select(c => c.Name).ToList();

        var rows = new List<IReadOnlyList<string>>();
        var columnList = string.Join(", ", columns.Select(c => $"`{c}`"));

        using var view = db.OpenView($"SELECT {columnList} FROM `{tableName}`");
        view.Execute();

        foreach (var record in view)
        {
            using (record)
            {
                var row = new List<string>();
                for (int i = 1; i <= columns.Count; i++)
                {
                    row.Add(record.GetString(i) ?? string.Empty);
                }
                rows.Add(row);
            }
        }

        return (columns, rows);
    }

    /// <summary>
    /// List all files in the MSI File table.
    /// </summary>
    public IReadOnlyList<MsiFileEntry> ListFiles(string msiPath)
    {
        var files = new List<MsiFileEntry>();

        using var db = new Database(msiPath, DatabaseOpenMode.ReadOnly);

        if (!db.Tables.Contains("File"))
            return files;

        using var view = db.OpenView(
            "SELECT `File`, `FileName`, `FileSize`, `Version`, `Component_` FROM `File`");
        view.Execute();

        foreach (var record in view)
        {
            using (record)
            {
                var fileName = record.GetString(2) ?? string.Empty;
                // MSI FileName format: "ShortName|LongName" — extract long name
                var longName = fileName.Contains('|') ? fileName.Split('|')[1] : fileName;

                files.Add(new MsiFileEntry
                {
                    FileKey = record.GetString(1) ?? string.Empty,
                    FileName = longName,
                    FileSize = record.GetInteger(3),
                    Version = record.GetString(4) ?? string.Empty,
                    ComponentKey = record.GetString(5) ?? string.Empty
                });
            }
        }

        return files;
    }

    private static string? ReadProperty(Database db, string propertyName)
    {
        if (!db.Tables.Contains("Property"))
            return null;

        try
        {
            return db.ExecuteScalar(
                "SELECT `Value` FROM `Property` WHERE `Property` = '{0}'",
                propertyName)?.ToString();
        }
        catch
        {
            return null;
        }
    }
}

public class MsiFileEntry
{
    public string FileKey { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public int FileSize { get; set; }
    public string Version { get; set; } = string.Empty;
    public string ComponentKey { get; set; } = string.Empty;
}
