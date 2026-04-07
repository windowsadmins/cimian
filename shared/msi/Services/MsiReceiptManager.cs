using Cimian.Msi.Models;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cimian.Msi.Services;

/// <summary>
/// Manages Cimian receipt files at %ProgramData%\ManagedInstalls\Receipts\.
/// Receipts provide a reliable Cimian-controlled installation record,
/// supplementing the Windows Installer registry which can be inconsistent.
/// </summary>
public class MsiReceiptManager
{
    private readonly ILogger<MsiReceiptManager> _logger;
    private readonly string _receiptsPath;
    private readonly ISerializer _serializer;
    private readonly IDeserializer _deserializer;

    public MsiReceiptManager(ILogger<MsiReceiptManager> logger, string? receiptsPath = null)
    {
        _logger = logger;
        _receiptsPath = receiptsPath
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ManagedInstalls", "Receipts");

        _serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();

        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    /// <summary>
    /// Write a receipt after successful installation.
    /// </summary>
    public void WriteReceipt(MsiReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentException.ThrowIfNullOrWhiteSpace(receipt.Product.Identifier);

        Directory.CreateDirectory(_receiptsPath);

        var fileName = SanitizeFileName(receipt.Product.Identifier) + ".yaml";
        var filePath = Path.Combine(_receiptsPath, fileName);

        var yaml = _serializer.Serialize(receipt);
        File.WriteAllText(filePath, yaml);

        _logger.LogDebug("Receipt written: {Path}", filePath);
    }

    /// <summary>
    /// Read a receipt by product identifier.
    /// </summary>
    public MsiReceipt? ReadReceipt(string identifier)
    {
        var fileName = SanitizeFileName(identifier) + ".yaml";
        var filePath = Path.Combine(_receiptsPath, fileName);

        if (!File.Exists(filePath))
            return null;

        try
        {
            var yaml = File.ReadAllText(filePath);
            return _deserializer.Deserialize<MsiReceipt>(yaml);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to read receipt {Path}: {Error}", filePath, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Delete a receipt (e.g., after uninstall).
    /// </summary>
    public bool DeleteReceipt(string identifier)
    {
        var fileName = SanitizeFileName(identifier) + ".yaml";
        var filePath = Path.Combine(_receiptsPath, fileName);

        if (!File.Exists(filePath))
            return false;

        File.Delete(filePath);
        _logger.LogDebug("Receipt deleted: {Path}", filePath);
        return true;
    }

    /// <summary>
    /// List all receipts.
    /// </summary>
    public IReadOnlyList<MsiReceipt> ListReceipts()
    {
        if (!Directory.Exists(_receiptsPath))
            return [];

        var receipts = new List<MsiReceipt>();
        foreach (var file in Directory.GetFiles(_receiptsPath, "*.yaml"))
        {
            try
            {
                var yaml = File.ReadAllText(file);
                var receipt = _deserializer.Deserialize<MsiReceipt>(yaml);
                if (receipt != null)
                    receipts.Add(receipt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to read receipt {Path}: {Error}", file, ex.Message);
            }
        }

        return receipts;
    }

    /// <summary>
    /// Create a receipt from MSI metadata after installation.
    /// </summary>
    public static MsiReceipt CreateFromMetadata(MsiMetadata metadata, string? installLocation = null)
    {
        return new MsiReceipt
        {
            Product = new MsiReceiptProduct
            {
                Name = metadata.ProductName,
                Version = metadata.FullVersion,
                Identifier = metadata.Identifier,
                Developer = metadata.Manufacturer,
            },
            Msi = new MsiReceiptInstaller
            {
                ProductCode = metadata.ProductCode,
                UpgradeCode = metadata.UpgradeCode,
                MsiVersion = metadata.ProductVersion,
                FullVersion = metadata.FullVersion,
            },
            Install = new MsiReceiptInstall
            {
                Date = DateTime.UtcNow.ToString("O"),
                Method = "msi-native",
                InstallLocation = installLocation ?? metadata.InstallLocation,
            }
        };
    }

    private static string SanitizeFileName(string identifier)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new char[identifier.Length];
        for (int i = 0; i < identifier.Length; i++)
        {
            sanitized[i] = Array.IndexOf(invalid, identifier[i]) >= 0 ? '_' : identifier[i];
        }
        return new string(sanitized);
    }
}
