// SoftwarePage.xaml.cs - Code-behind for Software page (WinUI 3)
// Microsoft Store-style design with banner carousel and category pills

using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Cimian.Core.Branding;
using Microsoft.UI.Xaml.Automation;
using Cimian.GUI.ManagedSoftwareCenter.Models;
using Cimian.GUI.ManagedSoftwareCenter.Services;
using Cimian.GUI.ManagedSoftwareCenter.ViewModels;

namespace Cimian.GUI.ManagedSoftwareCenter.Views;

/// <summary>
/// Software page - browse all available optional software
/// Microsoft Store-style design with carousel and category pills
/// </summary>
public partial class SoftwarePage : Page
{
    public SoftwareViewModel ViewModel { get; }
    
    // Carousel state
    private readonly DispatcherTimer _carouselTimer;
    private int _currentSlide = 0;
    private readonly List<Image> _bannerImages = new();
    private readonly List<Ellipse> _indicators = new();
    private IReadOnlyList<ResolvedBanner> _banners = [];
    private string? _selectedCategory;

    public SoftwarePage()
    {
        ViewModel = App.GetService<SoftwareViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        
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
                // Resolve hero title, accent and banners from branding.yaml / Policies\Cimian.
                await ApplyBrandingAsync();
                
                // Start carousel if we have images
                if (_banners.Count > 1)
                {
                    _carouselTimer.Start();
                }
                
                // Load data - bindings will auto-update
                await ViewModel.LoadAsync();

                // Apply category passed via navigation (e.g. cimian://category/Browsers).
                // Canonicalize against the loaded category list so pill highlighting and
                // filtering work regardless of the casing used in the deep link.
                if (!string.IsNullOrEmpty(_selectedCategory) &&
                    !string.Equals(_selectedCategory, "All", StringComparison.OrdinalIgnoreCase))
                {
                    var canonical = ViewModel.Categories?.FirstOrDefault(c =>
                        string.Equals(c, _selectedCategory, StringComparison.OrdinalIgnoreCase));
                    if (canonical != null)
                    {
                        _selectedCategory = canonical;
                        ViewModel.SelectedCategory = canonical;
                        SectionHeader.Text = canonical;
                    }
                    else
                    {
                        // Unknown category name -- fall back to showing everything.
                        _selectedCategory = "All";
                    }
                }
                else
                {
                    _selectedCategory = "All";
                }

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

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Only honor a typed CategoryNavigationRequest. ShellViewModel.NavigationParameter
        // also flows through here as a raw string (it can carry an item name set by
        // NavigateToItemDetail), so plain strings must not be treated as categories.
        if (e.Parameter is CategoryNavigationRequest request &&
            !string.IsNullOrWhiteSpace(request.CategoryName))
        {
            _selectedCategory = request.CategoryName;
        }
    }

    #region Branding Images & Carousel

    /// <summary>
    /// Applies the hero title (default / app_title / banner_title), the accent gradient
    /// behind it, and the banner slides. A blank title collapses the overlay so a custom
    /// image can stand alone.
    /// </summary>
    private async Task ApplyBrandingAsync()
    {
        var branding = App.GetService<IBrandingService>();
        var current = branding.Reload();

        HeroTitleText.Text = current.BannerTitle;
        var visibility = current.ShowBannerTitle ? Visibility.Visible : Visibility.Collapsed;
        HeroTitleText.Visibility = visibility;
        HeroTitlePanel.Visibility = visibility;

        if (BrandingTheme.HeroGradient(current.Accent) is { } gradient)
            HeroGradientBorder.Background = gradient;

        // The Featured tile follows the brand; the Updates tile keeps its orange,
        // which reads as "needs attention" whatever the brand colour.
        if (BrandingTheme.TileGradient(current.Accent) is { } tile)
            FeaturedTile.Background = tile;

        await LoadBannersAsync(branding, current.Banners);
    }

    /// <summary>
    /// Builds one slide per banner, as Munki's showcase does. Slides with a link make the
    /// banner clickable while they are showing.
    /// </summary>
    private async Task LoadBannersAsync(IBrandingService branding, IReadOnlyList<ResolvedBanner> banners)
    {
        CarouselContainer.Children.Clear();
        CarouselIndicators.Children.Clear();
        _bannerImages.Clear();
        _indicators.Clear();
        _currentSlide = 0;

        var loaded = new List<ResolvedBanner>();
        foreach (var banner in banners)
        {
            var bitmap = await branding.LoadImageAsync(banner.ImagePath);
            if (bitmap == null)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load branding image: {banner.ImagePath}");
                continue;
            }

            var index = loaded.Count;
            var image = new Image
            {
                Source = bitmap,
                Stretch = Stretch.UniformToFill,
                Opacity = index == 0 ? 1 : 0,
            };
            AutomationProperties.SetName(image, banner.Link != null
                ? $"Banner {index + 1}, opens {banner.Link.Uri}"
                : $"Banner {index + 1}");
            CarouselContainer.Children.Add(image);
            _bannerImages.Add(image);

            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(Microsoft.UI.Colors.White),
                Margin = new Thickness(4, 0, 4, 0),
            };
            dot.Tapped += (_, e) =>
            {
                e.Handled = true;
                ShowSlide(index);
            };
            CarouselIndicators.Children.Add(dot);
            _indicators.Add(dot);

            loaded.Add(banner);
        }

        _banners = loaded;
        UpdateCarouselIndicators();
        System.Diagnostics.Debug.WriteLine($"Loaded {_banners.Count} branding images");
    }

    private void CarouselTimer_Tick(object? sender, object e)
    {
        if (_banners.Count < 2) return;
        AnimateToSlide((_currentSlide + 1) % _banners.Count);
    }

    /// <summary>Jumps to a slide and restarts the timer, so a chosen slide stays up.</summary>
    private void ShowSlide(int index)
    {
        if (index < 0 || index >= _banners.Count || index == _currentSlide) return;
        _carouselTimer.Stop();
        AnimateToSlide(index);
        if (_banners.Count > 1) _carouselTimer.Start();
    }

    private void AnimateToSlide(int slideIndex)
    {
        var duration = TimeSpan.FromMilliseconds(500);
        
        for (int i = 0; i < _bannerImages.Count; i++)
        {
            var targetOpacity = (i == slideIndex) ? 1.0 : 0.0;
            var storyboard = new Storyboard();
            var animation = new DoubleAnimation
            {
                To = targetOpacity,
                Duration = new Duration(duration),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(animation, _bannerImages[i]);
            Storyboard.SetTargetProperty(animation, "Opacity");
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        _currentSlide = slideIndex;
        UpdateCarouselIndicators();
    }

    private void UpdateCarouselIndicators()
    {
        // Hide indicators if only one image or none
        CarouselIndicators.Visibility = _banners.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        
        for (int i = 0; i < _indicators.Count; i++)
        {
            _indicators[i].Opacity = (i == _currentSlide) ? 1.0 : 0.5;
        }
    }

    /// <summary>
    /// Opens the showing slide's link: cimian:// (or a translated munki://) navigates inside
    /// the app, http(s) opens the default browser.
    /// </summary>
    private async void HeroBanner_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_currentSlide >= _banners.Count || _banners[_currentSlide].Link is not { } link)
            return;

        e.Handled = true;
        try
        {
            if (link.Kind == BrandingLinkKind.Internal)
                App.HandleProtocolActivation(link.Uri);
            else
                await Windows.System.Launcher.LaunchUriAsync(link.Uri);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open banner link {link.Uri}: {ex.Message}");
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
            var isSelected = category == (_selectedCategory ?? "All");
            var button = new Button
            {
                Tag = category,
                Style = isSelected
                    ? (Style)this.Resources["CategoryPillButtonSelected"]
                    : (Style)this.Resources["CategoryPillButton"]
            };
            // Build content after the Button exists so the icon/text can bind to Button.Foreground,
            // which carries the ThemeResource set by the style and stays correct across theme changes.
            button.Content = BuildCategoryContent(button, category);
            button.Click += CategoryPill_Click;
            CategoryPillsPanel.Children.Add(button);
        }
    }

    private static StackPanel BuildCategoryContent(Button host, string category)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        var foregroundBinding = new Microsoft.UI.Xaml.Data.Binding
        {
            Source = host,
            Path = new PropertyPath("Foreground"),
            Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay
        };

        var icon = new FontIcon
        {
            FontSize = 14,
            Margin = new Thickness(0, 0, 6, 0),
            Glyph = GetCategoryIcon(category)
        };
        icon.SetBinding(FontIcon.ForegroundProperty, foregroundBinding);
        panel.Children.Add(icon);

        var text = new TextBlock { Text = category };
        text.SetBinding(TextBlock.ForegroundProperty, foregroundBinding);
        panel.Children.Add(text);

        return panel;
    }

    private static string GetCategoryIcon(string category)
    {
        // Map categories to Segoe MDL2 Assets icons
        return ViewModels.CategoryIcons.GetGlyph(category);
    }

    private void CategoryPill_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string category)
        {
            SelectCategory(category);
        }
    }

    /// <summary>
    /// Selects a category pill, updates the All-apps section header, and syncs pill styles.
    /// </summary>
    private void SelectCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category))
            category = "All";

        _selectedCategory = category;
        ViewModel.SelectedCategory = category;

        // The Featured section has its own heading; this header is always the main app list.
        SectionHeader.Text = IsAllCategory(category) ? "All apps" : category;

        foreach (var child in CategoryPillsPanel.Children)
        {
            if (child is Button btn)
            {
                var isSelected = (string?)btn.Tag == category;
                btn.Style = isSelected
                    ? (Style)this.Resources["CategoryPillButtonSelected"]
                    : (Style)this.Resources["CategoryPillButton"];
            }
        }

        // Featured is an All-only strip; hide it when browsing a specific category.
        UpdateFeaturedSectionVisibility();
    }

    private void FeaturedButton_Click(object sender, TappedRoutedEventArgs e)
    {
        // Banner "Featured" tile: jump to the Featured section on the Software page.
        // Do not relabel the All-apps list — Featured already has its own heading above it.
        SelectCategory("All");

        // Defer scroll until after Featured becomes visible and layout runs (e.g. coming
        // from a category where the section was collapsed).
        DispatcherQueue.TryEnqueue(() =>
        {
            if (FeaturedSection.Visibility != Visibility.Visible)
                return;

            FeaturedSection.StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = true,
                VerticalAlignmentRatio = 0.0 // Pin the Featured heading near the top of the viewport
            });
        });
    }

    private void UpdatesButton_Click(object sender, TappedRoutedEventArgs e)
    {
        if (App.MainWindow is MainWindow mainWindow)
            mainWindow.NavigateToPage("updates");
    }

    #endregion

    #region UI State Management

    private static bool IsAllCategory(string? category) =>
        string.IsNullOrEmpty(category) ||
        string.Equals(category, "All", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Featured items only belong on the All browse view — not under category filters.
    /// </summary>
    private void UpdateFeaturedSectionVisibility()
    {
        var showFeatured = ViewModel.HasFeaturedItems
            && !ViewModel.IsLoading
            && IsAllCategory(_selectedCategory ?? ViewModel.SelectedCategory);

        FeaturedSection.Visibility = showFeatured ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateUIState()
    {
        LoadingIndicator.IsActive = ViewModel.IsLoading;
        LoadingIndicator.Visibility = ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        
        var hasItems = ViewModel.Items?.Count > 0;
        SoftwareGrid.Visibility = hasItems && !ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = !hasItems && !ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        UpdateFeaturedSectionVisibility();
        
        if (!hasItems)
        {
            EmptyMessageText.Text = ViewModel.EmptyMessage;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(ViewModel.IsLoading):
                case nameof(ViewModel.IsEmpty):
                case nameof(ViewModel.Items):
                case nameof(ViewModel.HasFeaturedItems):
                    UpdateUIState();
                    break;
                case nameof(ViewModel.EmptyMessage):
                    EmptyMessageText.Text = ViewModel.EmptyMessage;
                    break;
                case nameof(ViewModel.Categories):
                    BuildCategoryPills();
                    break;
                case nameof(ViewModel.SelectedCategory):
                    SectionHeader.Text = IsAllCategory(ViewModel.SelectedCategory)
                        ? "All apps"
                        : ViewModel.SelectedCategory;
                    UpdateFeaturedSectionVisibility();
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

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is InstallableItem item)
        {
            if (App.MainWindow is MainWindow mainWindow)
            {
                mainWindow.NavigateToItemDetail(item.Name);
            }
        }
    }

    #endregion
}
