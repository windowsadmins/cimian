using System.Text;
using System.Text.Json;
using Cimian.Msi.Services;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cimian.CLI.Pkginspector.Services;

/// <summary>
/// Inspects MSI packages and displays their contents.
/// </summary>
public class MsiInspector
{
    private readonly MsiPropertyReader _reader;
    private readonly MsiReceiptManager _receiptManager;

    public MsiInspector(MsiPropertyReader reader, MsiReceiptManager receiptManager)
    {
        _reader = reader;
        _receiptManager = receiptManager;
    }

    /// <summary>
    /// Show a summary of the MSI package metadata.
    /// </summary>
    public void Inspect(string msiPath, bool asYaml, bool asJson)
    {
        var metadata = _reader.ReadMetadata(msiPath);

        if (asYaml)
        {
            var serializer = new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
                .Build();
            Console.WriteLine(serializer.Serialize(metadata));
            return;
        }

        if (asJson)
        {
            var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            Console.WriteLine(JsonSerializer.Serialize(metadata, options));
            return;
        }

        // Human-readable output
        Console.WriteLine($"Package:      {Path.GetFileName(msiPath)}");
        Console.WriteLine($"Product:      {metadata.ProductName}");
        Console.WriteLine($"Version:      {metadata.ProductVersion}");
        if (metadata.FullVersion != metadata.ProductVersion)
            Console.WriteLine($"Full Version: {metadata.FullVersion}");
        Console.WriteLine($"Manufacturer: {metadata.Manufacturer}");
        Console.WriteLine($"ProductCode:  {metadata.ProductCode}");
        Console.WriteLine($"UpgradeCode:  {metadata.UpgradeCode}");

        if (!string.IsNullOrEmpty(metadata.Architecture))
            Console.WriteLine($"Architecture: {metadata.Architecture}");

        if (!string.IsNullOrEmpty(metadata.Description))
            Console.WriteLine($"Description:  {metadata.Description}");

        if (metadata.IsCimianPackage)
        {
            Console.WriteLine();
            Console.WriteLine("Cimian Package: Yes");
            Console.WriteLine($"Identifier:   {metadata.Identifier}");
            Console.WriteLine();
            Console.WriteLine("--- Embedded build-info.yaml ---");
            Console.WriteLine(metadata.BuildInfoYaml);
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("Cimian Package: No (commercial MSI)");
        }

        // Show file count
        var files = _reader.ListFiles(msiPath);
        Console.WriteLine();
        Console.WriteLine($"Files: {files.Count}");
        if (files.Count > 0 && files.Count <= 20)
        {
            foreach (var file in files)
                Console.WriteLine($"  {file.FileName} ({file.FileSize:N0} bytes)");
        }
        else if (files.Count > 20)
        {
            foreach (var file in files.Take(10))
                Console.WriteLine($"  {file.FileName} ({file.FileSize:N0} bytes)");
            Console.WriteLine($"  ... and {files.Count - 10} more files");
        }

        // Show table count
        var tables = _reader.ListTables(msiPath);
        Console.WriteLine();
        Console.WriteLine($"Tables: {tables.Count} ({string.Join(", ", tables)})");
    }

    /// <summary>
    /// List all tables in the MSI database.
    /// </summary>
    public void ListTables(string msiPath)
    {
        var tables = _reader.ListTables(msiPath);
        foreach (var table in tables)
            Console.WriteLine(table);
    }

    /// <summary>
    /// Dump the contents of a specific table.
    /// </summary>
    public void DumpTable(string msiPath, string tableName)
    {
        var (columns, rows) = _reader.ReadTable(msiPath, tableName);

        // Print header
        Console.WriteLine(string.Join("\t", columns));
        Console.WriteLine(new string('-', columns.Sum(c => c.Length + 8)));

        // Print rows
        foreach (var row in rows)
        {
            Console.WriteLine(string.Join("\t", row));
        }

        Console.WriteLine();
        Console.WriteLine($"{rows.Count} row(s)");
    }

    /// <summary>
    /// List all files in the File table.
    /// </summary>
    public void ListFiles(string msiPath)
    {
        var files = _reader.ListFiles(msiPath);

        Console.WriteLine($"{"File",-40} {"Size",12} {"Version",-20} {"Component",-30}");
        Console.WriteLine(new string('-', 102));

        long totalSize = 0;
        foreach (var file in files)
        {
            Console.WriteLine($"{file.FileName,-40} {file.FileSize,12:N0} {file.Version,-20} {file.ComponentKey,-30}");
            totalSize += file.FileSize;
        }

        Console.WriteLine();
        Console.WriteLine($"{files.Count} file(s), {totalSize:N0} bytes total");
    }

    /// <summary>
    /// Validate MSI structure.
    /// </summary>
    public void Verify(string msiPath)
    {
        var issues = new List<string>();
        var warnings = new List<string>();

        try
        {
            var metadata = _reader.ReadMetadata(msiPath);

            // Check required properties
            if (string.IsNullOrEmpty(metadata.ProductName))
                issues.Add("Missing ProductName property");
            if (string.IsNullOrEmpty(metadata.ProductVersion))
                issues.Add("Missing ProductVersion property");
            if (string.IsNullOrEmpty(metadata.ProductCode))
                issues.Add("Missing ProductCode property");
            if (string.IsNullOrEmpty(metadata.UpgradeCode))
                warnings.Add("Missing UpgradeCode (upgrades won't work)");
            if (string.IsNullOrEmpty(metadata.Manufacturer))
                warnings.Add("Missing Manufacturer");

            // Check tables
            var tables = _reader.ListTables(msiPath);
            var requiredTables = new[] { "Property", "Feature" };
            foreach (var table in requiredTables)
            {
                if (!tables.Contains(table))
                    issues.Add($"Missing required table: {table}");
            }

            // Check Cimian metadata
            if (metadata.IsCimianPackage)
            {
                Console.WriteLine("Cimian package detected - checking extended metadata...");
                if (string.IsNullOrEmpty(metadata.Identifier))
                    warnings.Add("Cimian package missing CIMIAN_IDENTIFIER");
                if (string.IsNullOrEmpty(metadata.FullVersion))
                    warnings.Add("Cimian package missing CIMIAN_FULL_VERSION");
            }

            // Report
            if (issues.Count == 0 && warnings.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("MSI validation passed - no issues found");
                Console.ResetColor();
            }
            else
            {
                foreach (var issue in issues)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  ERROR: {issue}");
                    Console.ResetColor();
                }
                foreach (var warning in warnings)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"  WARN:  {warning}");
                    Console.ResetColor();
                }
            }

            Console.WriteLine();
            Console.WriteLine($"Product: {metadata.ProductName} {metadata.ProductVersion}");
            Console.WriteLine($"Tables:  {tables.Count}");
            Console.WriteLine($"Files:   {_reader.ListFiles(msiPath).Count}");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Failed to open MSI: {ex.Message}");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Show an installed receipt by identifier.
    /// </summary>
    public void ShowReceipt(string identifier)
    {
        var receipt = _receiptManager.ReadReceipt(identifier);
        if (receipt == null)
        {
            Console.WriteLine($"No receipt found for: {identifier}");
            return;
        }

        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
        Console.WriteLine(serializer.Serialize(receipt));
    }
}
