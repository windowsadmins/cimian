// ItemDetailPage.xaml.cs - Code-behind for Item Detail page (WinUI 3)

using System.IO;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media.Imaging;
using Cimian.GUI.ManagedSoftwareCenter.ViewModels;
using Cimian.GUI.ManagedSoftwareCenter.Models;
using Cimian.GUI.ManagedSoftwareCenter.Services;

namespace Cimian.GUI.ManagedSoftwareCenter.Views;

/// <summary>
/// Item Detail page - shows full details for a software item
/// </summary>
public partial class ItemDetailPage : Page
{
    public ItemDetailViewModel ViewModel { get; }
    private object? _navigationParameter;

    public ItemDetailPage()
    {
        ViewModel = App.GetService<ItemDetailViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        
        Loaded += async (s, e) =>
        {
            if (_navigationParameter is InstallableItem item)
            {
                await ViewModel.LoadAsync(item);
            }
            else if (_navigationParameter is string itemName)
            {
                await ViewModel.LoadAsync(itemName);
            }
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _navigationParameter = e.Parameter;
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(ViewModel.IsLoading):
                    LoadingIndicator.IsActive = ViewModel.IsLoading;
                    LoadingIndicator.Visibility = ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case nameof(ViewModel.Item):
                    UpdateItemDisplay();
                    break;
                case nameof(ViewModel.ShowInstallButton):
                    InstallButton.Visibility = ViewModel.ShowInstallButton ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case nameof(ViewModel.ShowRemoveButton):
                    RemoveButton.Visibility = ViewModel.ShowRemoveButton ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case nameof(ViewModel.ShowCancelButton):
                    CancelButton.Visibility = ViewModel.ShowCancelButton ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case nameof(ViewModel.ShowStatusBadge):
                    StatusBadge.Visibility = ViewModel.ShowStatusBadge ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case nameof(ViewModel.StatusText):
                    StatusText.Text = ViewModel.StatusText;
                    break;
            }
        });
    }

    private void UpdateItemDisplay()
    {
        var item = ViewModel.Item;
        if (item == null) return;

        TitleText.Text = item.DisplayName ?? "Software Details";
        DisplayNameText.Text = item.DisplayName ?? "";
        DeveloperText.Text = item.Developer ?? "";
        DeveloperText.Visibility = string.IsNullOrEmpty(item.Developer) ? Visibility.Collapsed : Visibility.Visible;
        VersionText.Text = item.Version ?? "";
        SizeText.Text = FormatFileSize(item.InstallerSize);
        CategoryText.Text = item.Category ?? "";
        DescriptionText.Text = item.Description ?? "";
        
        // Info section
        InfoVersionText.Text = item.Version ?? "";
        InfoCategoryText.Text = item.Category ?? "";
        InfoSizeText.Text = FormatFileSize(item.InstallerSize);
        
        // Installed version
        if (!string.IsNullOrEmpty(item.InstalledVersion))
        {
            InstalledVersionPanel.Visibility = Visibility.Visible;
            InstalledVersionText.Text = item.InstalledVersion;
        }
        else
        {
            InstalledVersionPanel.Visibility = Visibility.Collapsed;
        }
        
        // Developer
        if (!string.IsNullOrEmpty(item.Developer))
        {
            DeveloperPanel.Visibility = Visibility.Visible;
            InfoDeveloperText.Text = item.Developer;
        }
        else
        {
            DeveloperPanel.Visibility = Visibility.Collapsed;
        }
        
        // Restart required
        RestartPanel.Visibility = item.RestartRequired ? Visibility.Visible : Visibility.Collapsed;
        
        // Deadline warning
        if (item.HasDeadline && item.DeadlineText != null)
        {
            DeadlineWarning.Visibility = Visibility.Visible;
            DeadlineText.Text = item.DeadlineText;
        }
        else
        {
            DeadlineWarning.Visibility = Visibility.Collapsed;
        }

        // Dependency info
        if (item.DependentItems is { Count: > 0 })
        {
            DependencyInfo.Visibility = Visibility.Visible;
            DependencyText.Text = $"Required by: {string.Join(", ", item.DependentItems)}";
        }
        else
        {
            DependencyInfo.Visibility = Visibility.Collapsed;
        }

        // Unavailable reason
        if (item.Status == ItemStatus.Unavailable && !string.IsNullOrEmpty(item.Note))
        {
            UnavailableInfo.Visibility = Visibility.Visible;
            UnavailableText.Text = item.Note;
        }
        else
        {
            UnavailableInfo.Visibility = Visibility.Collapsed;
        }
        
        // Release notes (from ReleaseNotes or Notes)
        var releaseNotes = item.ReleaseNotes ?? item.Notes;
        if (!string.IsNullOrEmpty(releaseNotes))
        {
            WhatsNewPanel.Visibility = Visibility.Visible;
            SetReleaseNotesContent(releaseNotes);
        }
        else
        {
            WhatsNewPanel.Visibility = Visibility.Collapsed;
        }
        
        // Screenshots
        _ = LoadScreenshotsAsync(item);
        
        // Update action buttons
        InstallButton.Visibility = ViewModel.ShowInstallButton ? Visibility.Visible : Visibility.Collapsed;
        RemoveButton.Visibility = ViewModel.ShowRemoveButton ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = ViewModel.ShowCancelButton ? Visibility.Visible : Visibility.Collapsed;
        StatusBadge.Visibility = ViewModel.ShowStatusBadge ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = ViewModel.StatusText;
    }

    /// <summary>
    /// Renders release notes content into the RichTextBlock.
    /// Supports basic HTML tags (b, strong, i, em, br, p, li, ul, ol, h1-h6) and plain text.
    /// </summary>
    private void SetReleaseNotesContent(string content)
    {
        ReleaseNotesText.Blocks.Clear();

        // Detect if content looks like HTML
        if (content.Contains('<') && Regex.IsMatch(content, @"<\s*(p|br|li|ul|ol|b|strong|i|em|h[1-6])\b", RegexOptions.IgnoreCase))
        {
            ParseHtmlToBlocks(content);
        }
        else
        {
            // Plain text — split by newlines into paragraphs
            foreach (var line in content.Split('\n'))
            {
                var para = new Paragraph();
                var trimmed = line.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    para.Inlines.Add(new Run { Text = " " });
                }
                else
                {
                    para.Inlines.Add(new Run { Text = trimmed });
                }
                ReleaseNotesText.Blocks.Add(para);
            }
        }
    }

    private void ParseHtmlToBlocks(string html)
    {
        // Strip full document tags
        html = Regex.Replace(html, @"<\s*/?\s*(html|head|body|meta|title|style|script)[^>]*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        // Remove style/script content 
        html = Regex.Replace(html, @"<style[^>]*>.*?</style>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        html = Regex.Replace(html, @"<script[^>]*>.*?</script>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // Split into block-level chunks by <p>, <li>, <br>, <h1>-<h6>
        // Replace block tags with markers
        html = Regex.Replace(html, @"<\s*br\s*/?\s*>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<\s*/?\s*(p|div)\s*[^>]*>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<\s*li\s*[^>]*>", "\n\u2022 ", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<\s*/?\s*(ul|ol|li)\s*[^>]*>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<\s*h[1-6]\s*[^>]*>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<\s*/\s*h[1-6]\s*>", "\n", RegexOptions.IgnoreCase);

        // Process remaining inline tags into runs
        var lines = html.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var para = new Paragraph();
            ParseInlineHtml(line.Trim(), para.Inlines);
            if (para.Inlines.Count > 0)
                ReleaseNotesText.Blocks.Add(para);
        }
    }

    private static void ParseInlineHtml(string html, InlineCollection inlines)
    {
        // Process <b>/<strong>, <i>/<em>, and strip other tags
        var pos = 0;
        var bold = false;
        var italic = false;

        while (pos < html.Length)
        {
            var tagStart = html.IndexOf('<', pos);
            if (tagStart < 0)
            {
                AddRun(inlines, DecodeHtmlEntities(html[pos..]), bold, italic);
                break;
            }

            // Text before the tag
            if (tagStart > pos)
            {
                AddRun(inlines, DecodeHtmlEntities(html[pos..tagStart]), bold, italic);
            }

            var tagEnd = html.IndexOf('>', tagStart);
            if (tagEnd < 0)
            {
                // Malformed — just output the rest
                AddRun(inlines, DecodeHtmlEntities(html[tagStart..]), bold, italic);
                break;
            }

            var tag = html[(tagStart + 1)..tagEnd].Trim().ToLowerInvariant();
            if (tag is "b" or "strong")
                bold = true;
            else if (tag is "/b" or "/strong")
                bold = false;
            else if (tag is "i" or "em")
                italic = true;
            else if (tag is "/i" or "/em")
                italic = false;
            // All other tags are stripped

            pos = tagEnd + 1;
        }
    }

    private static void AddRun(InlineCollection inlines, string text, bool bold, bool italic)
    {
        if (string.IsNullOrEmpty(text)) return;
        var run = new Run { Text = text };
        if (bold) run.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        if (italic) run.FontStyle = Windows.UI.Text.FontStyle.Italic;
        inlines.Add(run);
    }

    private static string DecodeHtmlEntities(string text)
    {
        text = text.Replace("&amp;", "&");
        text = text.Replace("&lt;", "<");
        text = text.Replace("&gt;", ">");
        text = text.Replace("&quot;", "\"");
        text = text.Replace("&apos;", "'");
        text = text.Replace("&nbsp;", " ");
        // Numeric entities
        text = Regex.Replace(text, @"&#(\d+);", m =>
            ((char)int.Parse(m.Groups[1].Value)).ToString());
        text = Regex.Replace(text, @"&#x([0-9a-fA-F]+);", m =>
            ((char)int.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber)).ToString());
        return text;
    }

    private async Task LoadScreenshotsAsync(InstallableItem item)
    {
        if (item.Screenshots is not { Count: > 0 })
        {
            ScreenshotsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var iconService = App.GetService<IIconService>();
        var images = new List<BitmapImage>();
        var iconsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ManagedInstalls", "icons");

        foreach (var screenshot in item.Screenshots)
        {
            if (string.IsNullOrWhiteSpace(screenshot)) continue;

            var fullPath = Path.GetFullPath(Path.Combine(iconsDir, screenshot));
            // Ensure path stays within icons directory
            if (!fullPath.StartsWith(iconsDir, StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(fullPath)) continue;

            try
            {
                var bitmap = new BitmapImage();
                using var stream = File.OpenRead(fullPath);
                var memStream = new MemoryStream();
                await stream.CopyToAsync(memStream);
                memStream.Position = 0;
                await bitmap.SetSourceAsync(memStream.AsRandomAccessStream());
                images.Add(bitmap);
            }
            catch
            {
                // Skip unloadable screenshots
            }
        }

        if (images.Count > 0)
        {
            ScreenshotFlipView.ItemsSource = images;
            ScreenshotPips.NumberOfPages = images.Count;
            ScreenshotsPanel.Visibility = Visibility.Visible;
        }
        else
        {
            ScreenshotsPanel.Visibility = Visibility.Collapsed;
        }
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes <= 0) return "Unknown";
        string[] sizes = { "B", "KB", "MB", "GB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is MainWindow mainWindow)
        {
            mainWindow.NavigateBack();
        }
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.InstallCommand.Execute(null);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.RemoveCommand.Execute(null);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelCommand.Execute(null);
    }
}
