using System.Windows;
using System.Windows.Media;
using Cimian.Core.Branding;
using Microsoft.Win32;

namespace Cimian.Status
{
    public partial class App : Application
    {
        /// <summary>Branding shared with Managed Software Center (policy > branding.yaml > defaults).</summary>
        public MscBranding Branding { get; private set; } = MscBranding.Default;

        /// <summary>
        /// Reads branding and swaps the accent into the app resources. Program calls this
        /// after App.xaml loads and before the window is built, so the window picks it up.
        /// </summary>
        public void ApplyBranding()
        {
            try
            {
                Branding = MscBrandingResolver.Load();
            }
            catch
            {
                Branding = MscBranding.Default;
            }

            if (Branding.Accent is not { } accent)
                return;

            var color = Color.FromRgb(accent.R, accent.G, accent.B);
            var dark = accent.DarkShades()[0];
            var primary = Color.FromRgb(dark.R, dark.G, dark.B);

            Resources["AccentColor"] = color;
            Resources["AccentBrush"] = new SolidColorBrush(color);
            Resources["PrimaryColor"] = primary;
            Resources["PrimaryBrush"] = new SolidColorBrush(primary);
            ModernWpf.ThemeManager.Current.AccentColor = color;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // Set theme preference based on system settings
            SetThemeBasedOnSystemSettings();
            
            // Listen for system theme changes
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            
            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Unsubscribe from system events
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            base.OnExit(e);
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General)
            {
                // Update theme when system appearance changes
                SetThemeBasedOnSystemSettings();
            }
        }

        private void SetThemeBasedOnSystemSettings()
        {
            try
            {
                // Check Windows registry for dark mode preference
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var appsUseLightTheme = key?.GetValue("AppsUseLightTheme");
                
                if (appsUseLightTheme is int lightTheme)
                {
                    // If AppsUseLightTheme is 0, use dark theme; if 1, use light theme
                    ModernWpf.ThemeManager.Current.ApplicationTheme = lightTheme == 0 
                        ? ModernWpf.ApplicationTheme.Dark 
                        : ModernWpf.ApplicationTheme.Light;
                }
                else
                {
                    // Default to light theme if unable to detect
                    ModernWpf.ThemeManager.Current.ApplicationTheme = ModernWpf.ApplicationTheme.Light;
                }
            }
            catch
            {
                // Fallback to light theme if there's any issue reading the registry
                ModernWpf.ThemeManager.Current.ApplicationTheme = ModernWpf.ApplicationTheme.Light;
            }
        }
    }
}
