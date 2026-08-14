using System.Windows;
using Encodex.Services;
using Encodex.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Encodex
{
    public partial class MainWindow : FluentWindow
    {
        private readonly AppSettingsStore _settingsStore = new();

        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
        }

        private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm)
                return;

            vm.ToggleThemeCommand.Execute(null);
            // Keep the window's default backdrop (None): only swap the theme resources.
            ApplicationThemeManager.Apply(
                vm.IsLightTheme ? ApplicationTheme.Light : ApplicationTheme.Dark,
                WindowBackdropType.None,
                updateAccent: true);
            _settingsStore.Save(new AppSettings { IsLightTheme = vm.IsLightTheme });
        }
    }
}
