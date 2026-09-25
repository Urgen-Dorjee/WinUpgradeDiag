using System.Windows;
using System.Windows.Threading;

namespace WinUpgradeDiag.App
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            base.OnStartup(e);
        }

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            // Degrade, never crash: a technician on a broken machine needs the window to stay up.
            MessageBox.Show(
                "An unexpected error occurred. The tool will keep running.\n\n" + e.Exception.Message,
                "WinUpgradeDiag", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }
    }
}
