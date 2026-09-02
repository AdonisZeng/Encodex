using System.Text;
using System.Threading;
using System.Windows;
using Encodex.Resources;
using Encodex.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Encodex
{
    public partial class App : Application
    {
        /// <summary>Scoped to the logon session: two Windows users may each run their own copy.</summary>
        private const string SingleInstanceMutexName = @"Local\Encodex-SingleInstance";

        private Mutex? _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            // Headless CLI mode: no window is created, results go to stdout.
            // (WinExe output is still capturable when stdout is redirected.)
            // The single-instance guard is taken only after this early return so
            // script/CI runs are never blocked by an open GUI window.
            if (e.Args.Length > 0 && string.Equals(e.Args[0], "--cli", StringComparison.OrdinalIgnoreCase))
            {
                var exitCode = CliRunner.Run(e.Args.Skip(1).ToArray());
                Shutdown(exitCode);
                return;
            }

            // A second GUI instance would race the first one for file locks, and the
            // updater only waits for the process that launched it, so two running
            // copies can turn an update into a partial replacement.
            _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
            if (!isFirstInstance)
            {
                // Fully qualified: Wpf.Ui.Controls also declares a MessageBox type.
                System.Windows.MessageBox.Show(
                    Res.Upd_AlreadyRunning,
                    "Encodex",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                Shutdown(0);
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

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                // Only the owning thread may release it; a non-owner throws, which the
                // catch swallows so shutdown is never disrupted by cleanup.
                _singleInstanceMutex?.ReleaseMutex();
            }
            catch
            {
                // Not owned by this thread (or already released).
            }
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;

            base.OnExit(e);
        }
    }
}
