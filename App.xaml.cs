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

            // Headless CLI mode: no window is created, results go to stdout.
            // (WinExe output is still capturable when stdout is redirected.)
            if (e.Args.Length > 0 && string.Equals(e.Args[0], "--cli", StringComparison.OrdinalIgnoreCase))
            {
                var exitCode = CliRunner.Run(e.Args.Skip(1).ToArray());
                Shutdown(exitCode);
                return;
            }

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
