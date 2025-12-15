using System.ComponentModel.DataAnnotations;
using YamlDotNet.Serialization;

namespace Cimian.Core.Models;

/// <summary>
/// Represents a manifest that defines conditional package deployment
/// Migrated from Go struct: manifest.Manifest
/// </summary>
public class Manifest
{
    /// <summary>
    /// Manifest format version
    /// </summary>
    [YamlMember(Alias = "version")]
    public string Version { get; set; } = "1.0";

    /// <summary>
    /// Human-readable name for the manifest
    /// </summary>
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    /// <summary>
    /// Description of what this manifest does
    /// </summary>
    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    /// <summary>
    /// List of conditional items to evaluate and potentially install
    /// </summary>
    [Required]
    [YamlMember(Alias = "conditional_items")]
    public List<ConditionalItem> ConditionalItems { get; set; } = new();

    /// <summary>
    /// Global configuration that applies to all items
    /// </summary>
    [YamlMember(Alias = "configuration")]
    public Configuration? Configuration { get; set; }

    /// <summary>
    /// Metadata for the manifest
    /// </summary>
    [YamlMember(Alias = "metadata")]
    public Dictionary<string, object>? Metadata { get; set; }

    /// <summary>
    /// Gets all conditional items including nested ones
    /// </summary>
    public IEnumerable<ConditionalItem> GetAllConditionalItems()
    {
        foreach (var item in ConditionalItems)
        {
            yield return item;
            
            if (item.ConditionalItems != null)
            {
                foreach (var nestedItem in GetNestedItems(item.ConditionalItems))
                {
                    yield return nestedItem;
                }
            }
        }
    }

    /// <summary>
    /// Recursively gets nested conditional items
    /// </summary>
    private static IEnumerable<ConditionalItem> GetNestedItems(List<ConditionalItem> items)
    {
        foreach (var item in items)
        {
            yield return item;
            
            if (item.ConditionalItems != null)
            {
                foreach (var nestedItem in GetNestedItems(item.ConditionalItems))
                {
                    yield return nestedItem;
                }
            }
        }
    }
}

/// <summary>
/// Represents a conditional item that may be installed based on system conditions
/// Migrated from Go's conditional item structure
/// </summary>
public class ConditionalItem
{
    /// <summary>
    /// Condition expression to evaluate (NSPredicate-style)
    /// </summary>
    [YamlMember(Alias = "condition")]
    public string? Condition { get; set; }

    /// <summary>
    /// Name of the catalog item to install if condition is met
    /// </summary>
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    /// <summary>
    /// Specific version to install (optional)
    /// </summary>
    [YamlMember(Alias = "version")]
    public string? Version { get; set; }

    /// <summary>
    /// Nested conditional items for hierarchical logic
    /// </summary>
    [YamlMember(Alias = "conditional_items")]
    public List<ConditionalItem>? ConditionalItems { get; set; }

    /// <summary>
    /// Override installer arguments for this specific item
    /// </summary>
    [YamlMember(Alias = "installer_args")]
    public List<string>? InstallerArgs { get; set; }

    /// <summary>
    /// Override uninstaller arguments for this specific item
    /// </summary>
    [YamlMember(Alias = "uninstaller_args")]
    public List<string>? UninstallerArgs { get; set; }

    /// <summary>
    /// Custom pre-install script for this item
    /// </summary>
    [YamlMember(Alias = "preinstall_script")]
    public string? PreinstallScript { get; set; }

    /// <summary>
    /// Custom post-install script for this item
    /// </summary>
    [YamlMember(Alias = "postinstall_script")]
    public string? PostinstallScript { get; set; }

    /// <summary>
    /// Additional metadata for this conditional item
    /// </summary>
    [YamlMember(Alias = "metadata")]
    public Dictionary<string, object>? Metadata { get; set; }

    /// <summary>
    /// Whether this is a leaf item (has a name) or container (has nested items)
    /// </summary>
    public bool IsLeafItem => !string.IsNullOrEmpty(Name);

    /// <summary>
    /// Whether this item has nested conditional items
    /// </summary>
    public bool HasNestedItems => ConditionalItems?.Count > 0;

    /// <summary>
    /// Gets the full package identifier if this is a leaf item
    /// </summary>
    public string? GetPackageId()
    {
        if (!IsLeafItem) return null;
        return string.IsNullOrEmpty(Version) ? Name : $"{Name}-{Version}";
    }
}
