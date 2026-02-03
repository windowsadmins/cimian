// Services/ItemFilterService.cs - Package for filtering items based on --item flag (Go parity: pkg/filter)

using System;
using System.Collections.Generic;
using System.Linq;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.Core.Services;

namespace Cimian.CLI.managedsoftwareupdate.Services;

/// <summary>
/// Filters catalog items based on --item flag criteria (Go parity: pkg/filter/filter.go)
/// </summary>
public class ItemFilterService
{
    private readonly HashSet<string> _items;
    private readonly bool _hasFilter;

    /// <summary>
    /// Creates a new ItemFilterService with the specified item filter
    /// </summary>
    /// <param name="items">List of item names to filter for (case-insensitive)</param>
    public ItemFilterService(IEnumerable<string>? items)
    {
        _items = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        if (items != null)
        {
            foreach (var item in items)
            {
                // Handle comma-separated values in a single item
                foreach (var part in item.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    _items.Add(part);
                }
            }
        }
        
        _hasFilter = _items.Count > 0;
    }

    /// <summary>
    /// Returns true if any items are set in the filter
    /// </summary>
    public bool HasFilter => _hasFilter;

    /// <summary>
    /// Filters a list of CatalogItems to only include those matching the filter.
    /// If no filter is set, returns all items unchanged.
    /// </summary>
    public List<CatalogItem> FilterCatalogItems(List<CatalogItem> items)
    {
        if (!_hasFilter)
        {
            return items;
        }

        var filtered = items.Where(item => _items.Contains(item.Name)).ToList();
        
        if (filtered.Count > 0)
        {
            ConsoleLogger.Info($"Filtered to {filtered.Count} item(s) via --item: [{string.Join(", ", filtered.Select(i => i.Name))}]");
        }
        else if (items.Count > 0)
        {
            ConsoleLogger.Warn($"No items match --item filter: [{string.Join(", ", _items)}]");
            ConsoleLogger.Debug($"Available items: [{string.Join(", ", items.Select(i => i.Name))}]");
        }

        return filtered;
    }

    /// <summary>
    /// Returns true if the filter is active and should override checkonly behavior.
    /// When using --item flag, you typically want to test actual installation, not just check.
    /// (Go parity: ShouldOverrideCheckOnly in filter.go)
    /// </summary>
    public bool ShouldOverrideCheckOnly => _hasFilter;

    /// <summary>
    /// Gets the items in the filter
    /// </summary>
    public IReadOnlySet<string> Items => _items;

    /// <summary>
    /// Filters a list of ManifestItems to only include those matching the filter.
    /// If no filter is set, returns all items unchanged.
    /// (Go parity: Apply in filter.go - filters manifestItems early)
    /// </summary>
    public List<ManifestItem> FilterManifestItems(List<ManifestItem> items)
    {
        if (!_hasFilter)
        {
            return items;
        }

        var filtered = items.Where(item => _items.Contains(item.Name)).ToList();
        
        if (filtered.Count > 0)
        {
            ConsoleLogger.Info($"Filtered manifest to {filtered.Count} item(s) via --item: [{string.Join(", ", filtered.Select(i => i.Name))}]");
        }
        else if (items.Count > 0)
        {
            ConsoleLogger.Warn($"No manifest items match --item filter: [{string.Join(", ", _items)}]");
        }

        return filtered;
    }
}
