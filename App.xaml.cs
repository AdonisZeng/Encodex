using System.Text;
using System.Windows;
using Encodex.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Encodex
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            // Restore the persisted theme before the main window is created.
            // Unconditional apply keeps the resource dictionaries in sync with the
            // ViewModel's IsLightTheme state (dark by default).
            var settings = new AppSettingsStore().Load();
            ApplicationThemeManager.Apply(
                settings.IsLightTheme ? ApplicationTheme.Light : ApplicationTheme.Dark,
                WindowBackdropType.None,
                updateAccent: true);

            base.OnStartup(e);
        }
    }
}
