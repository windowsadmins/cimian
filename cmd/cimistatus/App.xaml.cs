using System.Windows;

namespace Cimian.Status
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Set theme preference based on system settings - use Light as default
            ModernWpf.ThemeManager.Current.ApplicationTheme = ModernWpf.ApplicationTheme.Light;
            
            base.OnStartup(e);
        }
    }
}
