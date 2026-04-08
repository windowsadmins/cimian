// CategoriesViewModel.cs - ViewModel for Categories page

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Cimian.GUI.ManagedSoftwareCenter.Models;
using Cimian.GUI.ManagedSoftwareCenter.Services;

namespace Cimian.GUI.ManagedSoftwareCenter.ViewModels;

/// <summary>
/// Category group with preview items
/// </summary>
public class CategoryGroup
{
    public string Name { get; set; } = string.Empty;
    public int ItemCount { get; set; }
    public List<InstallableItem> PreviewItems { get; set; } = [];
    public string IconGlyph => Views.SoftwarePage.GetCategoryIconGlyph(Name);
}

/// <summary>
/// ViewModel for the Categories page - browse software organized by category
/// </summary>
public partial class CategoriesViewModel : ObservableObject
{
    private readonly IInstallInfoService _installInfoService;
    private readonly IIconService _iconService;

    [ObservableProperty]
    public partial ObservableCollection<CategoryGroup> Categories { get; set; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial CategoryGroup? SelectedCategory { get; set; }

    public CategoriesViewModel(IInstallInfoService installInfoService, IIconService iconService)
    {
        _installInfoService = installInfoService;
        _iconService = iconService;
        _installInfoService.InstallInfoChanged += OnInstallInfoChanged;
    }

    /// <summary>
    /// Load categories with preview items
    /// </summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var categoryNames = await _installInfoService.GetCategoriesAsync();
            var groups = new List<CategoryGroup>();

            foreach (var categoryName in categoryNames)
            {
                var items = await _installInfoService.GetItemsByCategoryAsync(categoryName);
                
                groups.Add(new CategoryGroup
                {
                    Name = categoryName,
                    ItemCount = items.Count,
                    PreviewItems = items.Take(4).ToList() // Show up to 4 preview items
                });
            }

            // Also add "Uncategorized" if there are items without category
            var allOptional = await _installInfoService.GetOptionalInstallsAsync();
            var uncategorized = allOptional.Where(x => string.IsNullOrWhiteSpace(x.Category)).ToList();
            if (uncategorized.Count > 0)
            {
                groups.Add(new CategoryGroup
                {
                    Name = "Uncategorized",
                    ItemCount = uncategorized.Count,
                    PreviewItems = uncategorized.Take(4).ToList()
                });
            }

            Categories = new ObservableCollection<CategoryGroup>(groups.OrderBy(x => x.Name));
            IsEmpty = Categories.Count == 0;

            // Load icons for preview items
            foreach (var group in Categories)
            {
                foreach (var item in group.PreviewItems)
                {
                    item.IconImage = await _iconService.GetIconAsync(item.Name, item.Icon);
                }
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async void OnInstallInfoChanged(object? sender, InstallInfo info)
    {
        await LoadAsync();
    }
}
