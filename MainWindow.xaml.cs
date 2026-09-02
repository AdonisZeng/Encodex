using System.IO;
using System.Windows;
using Encodex.Resources;
using Encodex.Services;
using Encodex.ViewModels;
using Wpf.Ui.Controls;

namespace Encodex
{
    public partial class MainWindow : FluentWindow
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();

            // Flush the configuration on close: a user who picked a folder, changed the
            // target encoding or toggled extensions and quit must find those choices
            // restored on the next launch. (Previously settings were only saved on scan.)
            Closing += (_, _) => (DataContext as MainViewModel)?.SaveSettings();

            // Surface a failed previous update (updater died mid-replacement) once,
            // after the window is visible, so the user knows why the version is stale.
            var failedUpdate = AppUpdateService.DetectFailedUpdate();
            if (failedUpdate != null)
                Loaded += (_, _) => System.Windows.MessageBox.Show(
                    failedUpdate, Res.Upd_DialogTitle, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }

        /// <summary>Drop a folder anywhere on the window to select it as the source.
        /// Dropping a file selects its containing folder instead.</summary>
        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (DataContext is not MainViewModel vm)
                return;

            string? droppedFolder = null;
            if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
                e.Data.GetData(DataFormats.FileDrop) is string[] paths &&
                paths.Length > 0)
            {
                if (Directory.Exists(paths[0]))
                    droppedFolder = paths[0];
                else if (File.Exists(paths[0]))
                    droppedFolder = Path.GetDirectoryName(paths[0]);
            }

            if (droppedFolder != null)
            {
                vm.SourceFolderPath = droppedFolder;
                vm.StatusText = string.Format(Res.VM_Selected, droppedFolder);
            }
        }
    }
}
