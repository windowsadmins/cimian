// SoftwarePage.xaml.cs - Code-behind for Software page (WPF/ModernWpf)
// Microsoft Store-style design with banner carousel and category pills

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using ModernWpf.Controls;
using Cimian.GUI.SoftwareCenter.Models;
using Cimian.GUI.SoftwareCenter.ViewModels;

namespace Cimian.GUI.SoftwareCenter.Views;

/// <summary>
/// Software page - browse all available optional software
/// Microsoft Store-style design with carousel and category pills
/// </summary>
public partial class SoftwarePage : System.Windows.Controls.Page
{
    public SoftwareViewModel ViewModel { get; }
    
    // Carousel state
    private readonly DispatcherTimer _carouselTimer;
    private int _currentSlide = 0;
    private readonly Image[] _bannerImages = new Image[3];
    private readonly Ellipse[] _indicators = new Ellipse[3];
    private readonly List<string> _brandingImagePaths = new();
    private string? _selectedCategory;
    
    // Branding paths (like Munki's WebResources)
    private static readonly string[] BrandingSearchPaths = new[]
    {
        @"C:\ProgramData\ManagedInstalls\branding"
    };

    public SoftwarePage()
    {
        ViewModel = App.GetService<SoftwareViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        
        // Initialize carousel references
        _bannerImages[0] = BannerImage1;
        _bannerImages[1] = BannerImage2;
        _bannerImages[2] = BannerImage3;
        _indicators[0] = Indicator1;
        _indicators[1] = Indicator2;
        _indicators[2] = Indicator3;
        
        // Setup carousel timer (7.5 seconds like Munki)
        _carouselTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(7.5)
        };
        _carouselTimer.Tick += CarouselTimer_Tick;
        
        // Subscribe to ViewModel property changes for visibility updates
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        
        Loaded += async (s, e) =>
        {
            try
            {
                // Load branding images first
                LoadBrandingImages();
                
                // Start carousel if we have images
                if (_brandingImagePaths.Count > 1)
                {
                    _carouselTimer.Start();
                }
                
                // Load data - bindings will auto-update
                await ViewModel.LoadAsync();
                
                // Build category pills after data loaded
                BuildCategoryPills();
                
                // Update UI state
                UpdateUIState();
            }
            catch (Exception ex)
            {
                EmptyMessageText.Text = $"Error: {ex.Message}";
                EmptyState.Visibility = Visibility.Visible;
            }
        };
        
        Unloaded += (s, e) =>
        {
            _carouselTimer.Stop();
        };
    }

    #region Branding Images & Carousel

    private void LoadBrandingImages()
    {
        _brandingImagePaths.Clear();
        
        // Search for branding images in known paths
        foreach (var basePath in BrandingSearchPaths)
        {
            if (!Directory.Exists(basePath)) continue;
            
            // Look for branding*.jpg, branding*.png
            var imageFiles = Directory.GetFiles(basePath, "branding*.*")
                .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                           f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                           f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f)
                .Take(3)
                .ToList();
            
            if (imageFiles.Count > 0)
            {
                _brandingImagePaths.AddRange(imageFiles);
                break; // Use first directory that has images
            }
        }
        
        // Load images into Image controls
        for (int i = 0; i < _bannerImages.Length; i++)
        {
            if (i < _brandingImagePaths.Count)
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(_brandingImagePaths[i], UriKind.Absolute);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    _bannerImages[i].Source = bitmap;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to load branding image: {ex.Message}");
                }
            }
        }
        
        // Update indicator visibility based on image count
        UpdateCarouselIndicators();
        
        System.Diagnostics.Debug.WriteLine($"Loaded {_brandingImagePaths.Count} branding images");
    }

    private void CarouselTimer_Tick(object? sender, EventArgs e)
    {
        if (_brandingImagePaths.Count < 2) return;
        
        // Calculate next slide
        int nextSlide = (_currentSlide + 1) % Math.Min(_brandingImagePaths.Count, 3);
        
        // Animate transition
        AnimateToSlide(nextSlide);
        
        _currentSlide = nextSlide;
    }

    private void AnimateToSlide(int slideIndex)
    {
        var duration = TimeSpan.FromMilliseconds(500);
        
        for (int i = 0; i < _bannerImages.Length; i++)
        {
            var targetOpacity = (i == slideIndex) ? 1.0 : 0.0;
            var animation = new DoubleAnimation(targetOpacity, duration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            _bannerImages[i].BeginAnimation(OpacityProperty, animation);
        }
        
        UpdateCarouselIndicators();
    }

    private void UpdateCarouselIndicators()
    {
        int imageCount = Math.Min(_brandingImagePaths.Count, 3);
        
        // Hide indicators if only one image or none
        CarouselIndicators.Visibility = imageCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        
        for (int i = 0; i < _indicators.Length; i++)
        {
            _indicators[i].Visibility = i < imageCount ? Visibility.Visible : Visibility.Collapsed;
            _indicators[i].Opacity = (i == _currentSlide) ? 1.0 : 0.5;
        }
    }

    #endregion

    #region Category Pills

    private void BuildCategoryPills()
    {
        CategoryPillsPanel.Children.Clear();
        
        var categories = ViewModel.Categories?.ToList() ?? new List<string>();
        
        foreach (var category in categories)
        {
            var button = new Button
            {
                Content = GetCategoryContent(category),
                Tag = category,
                Style = category == (_selectedCategory ?? "All") 
                    ? (Style)FindResource("CategoryPillButtonSelected")
                    : (Style)FindResource("CategoryPillButton")
            };
            
            button.Click += CategoryPill_Click;
            CategoryPillsPanel.Children.Add(button);
        }
    }

    private object GetCategoryContent(string category)
    {
        // Create content with icon + text like Microsoft Store
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        
        // Add category icon
        var icon = new FontIcon
        {
            FontSize = 14,
            Margin = new Thickness(0, 0, 6, 0),
            Glyph = GetCategoryIcon(category)
        };
        panel.Children.Add(icon);
        
        var text = new TextBlock { Text = category };
        panel.Children.Add(text);
        
        return panel;
    }

    private static string GetCategoryIcon(string category)
    {
        // Map categories to Segoe MDL2 Assets icons
        return category.ToLowerInvariant() switch
        {
            "all" => "\uE8FD",           // Grid
            "productivity" => "\uE7C3",   // Edit
            "utilities" => "\uE90F",      // Repair
            "developer tools" => "\uE943", // Code
            "developer" => "\uE943",      // Code
            "communication" => "\uE8BD",  // Chat
            "media" => "\uE8B2",          // Play
            "entertainment" => "\uE7F4",  // TV
            "business" => "\uE821",       // Briefcase
            "security" => "\uE72E",       // Shield
            "photo & video" => "\uE722",  // Camera
            "music" => "\uE8D6",          // Music
            "creativity" => "\uE790",     // Brush
            "education" => "\uE7BE",      // Education
            "gaming" => "\uE7FC",         // Game
            _ => "\uE74C"                 // App (default)
        };
    }

    private void CategoryPill_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string category)
        {
            _selectedCategory = category;
            ViewModel.SelectedCategory = category;
            
            // Update header
            SectionHeader.Text = category == "All" ? "All apps" : category;
            
            // Update button styles
            foreach (var child in CategoryPillsPanel.Children)
            {
                if (child is Button btn)
                {
                    var isSelected = (string?)btn.Tag == category;
                    btn.Style = isSelected
                        ? (Style)FindResource("CategoryPillButtonSelected")
                        : (Style)FindResource("CategoryPillButton");
                }
            }
        }
    }

    private void FeaturedButton_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Navigate to featured items (could filter by featured_items from InstallInfo)
        // For now, just show all items
        _selectedCategory = "All";
        ViewModel.SelectedCategory = "All";
        SectionHeader.Text = "Featured";
    }

    private void UpdatesButton_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Navigate to Updates page
        var mainWindow = Window.GetWindow(this) as MainWindow;
        mainWindow?.NavigateToPage("updates");
    }

    #endregion

    #region UI State Management

    private void UpdateUIState()
    {
        LoadingIndicator.IsActive = ViewModel.IsLoading;
        LoadingIndicator.Visibility = ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        
        var hasItems = ViewModel.Items?.Count > 0;
        SoftwareGrid.Visibility = hasItems && !ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = !hasItems && !ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        
        if (!hasItems)
        {
            EmptyMessageText.Text = ViewModel.EmptyMessage;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(ViewModel.IsLoading):
                case nameof(ViewModel.IsEmpty):
                case nameof(ViewModel.Items):
                    UpdateUIState();
                    break;
                case nameof(ViewModel.EmptyMessage):
                    EmptyMessageText.Text = ViewModel.EmptyMessage;
                    break;
                case nameof(ViewModel.Categories):
                    BuildCategoryPills();
                    break;
                case nameof(ViewModel.SelectedCategory):
                    SectionHeader.Text = ViewModel.SelectedCategory == "All" || string.IsNullOrEmpty(ViewModel.SelectedCategory) 
                        ? "All apps" 
                        : ViewModel.SelectedCategory;
                    break;
            }
        });
    }

    #endregion

    #region Event Handlers

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            ViewModel.SearchText = sender.Text;
        }
    }

    private void OnItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is InstallableItem item)
        {
            // Navigate to item detail
            if (Application.Current is App app && app.MainWindow is MainWindow mainWindow)
            {
                mainWindow.NavigateToItemDetail(item.Name);
            }
        }
    }

    #endregion
}
