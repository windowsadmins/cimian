using WixToolset.Dtf.WindowsInstaller;

namespace Cimian.CLI.managedsoftwareupdate.Services;

/// <summary>
/// Selects maintenance arguments for an older installation of the same MSI product.
/// A fresh install must not receive REINSTALL=ALL: it has no installed features to reinstall.
/// </summary>
internal static class MsiUpgradePolicy
{
    internal static IReadOnlyList<string> GetAdditionalArgumentsForPackage(
        string msiPath, IReadOnlyList<string> configuredArguments)
    {
        using var database = new Database(msiPath, DatabaseOpenMode.ReadOnly);
        var productCode = database.ExecuteScalar(
            "SELECT `Value` FROM `Property` WHERE `Property` = 'ProductCode'") as string;
        var packageVersion = database.ExecuteScalar(
            "SELECT `Value` FROM `Property` WHERE `Property` = 'ProductVersion'") as string;
        if (!Guid.TryParse(productCode, out _))
            return Array.Empty<string>();

        // Query the package's actual ProductCode, not a related UpgradeCode
        // product: a different-product major upgrade must use normal /i.
        var installation = new ProductInstallation(productCode);
        if (!installation.IsInstalled)
            return Array.Empty<string>();

        return GetAdditionalArguments(productCode, packageVersion,
            installation.ProductCode, installation["VersionString"], configuredArguments);
    }

    internal static IReadOnlyList<string> GetAdditionalArguments(
        string? packageProductCode,
        string? packageVersion,
        string? installedProductCode,
        string? installedVersion,
        IEnumerable<string> configuredArguments)
    {
        // An authored maintenance mode takes precedence over automatic selection.
        if (configuredArguments.Any(argument =>
        {
            var property = argument.Trim().Trim('"').Split('=', 2)[0].Trim();
            return property.Equals("REINSTALL", StringComparison.OrdinalIgnoreCase) ||
                   property.Equals("REINSTALLMODE", StringComparison.OrdinalIgnoreCase);
        }))
            return Array.Empty<string>();

        if (!Guid.TryParse(packageProductCode, out var packageCode) ||
            !Guid.TryParse(installedProductCode, out var installedCode) ||
            packageCode != installedCode ||
            !Version.TryParse(packageVersion, out var incoming) ||
            !Version.TryParse(installedVersion, out var installed))
            return Array.Empty<string>();

        // Keep fourth-field build differences, but treat an omitted field as zero.
        static Version Normalize(Version version) => new(
            version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

        if (Normalize(incoming) <= Normalize(installed))
            return Array.Empty<string>();

        return new[] { "REINSTALL=ALL", "REINSTALLMODE=vomus" };
    }
}
