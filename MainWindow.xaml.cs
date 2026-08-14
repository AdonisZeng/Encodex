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

            // Surface a failed previous update (updater died mid-replacement) once,
            // after the window is visible, so the user knows why the version is stale.
            var failedUpdate = AppUpdateService.DetectFailedUpdate();
            if (failedUpdate != null)
                Loaded += (_, _) => System.Windows.MessageBox.Show(
                    failedUpdate, Res.Upd_DialogTitle, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }

        /// <summary>Drop a folder anywhere on the window to select it as the source.</summary>
        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (DataContext is not MainViewModel vm)
                return;

            if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
                e.Data.GetData(DataFormats.FileDrop) is string[] paths &&
                paths.Length > 0 &&
                Directory.Exists(paths[0]))
            {
                vm.SourceFolderPath = paths[0];
                vm.StatusText = string.Format(Res.VM_Selected, paths[0]);
            }
        }
    }
}
