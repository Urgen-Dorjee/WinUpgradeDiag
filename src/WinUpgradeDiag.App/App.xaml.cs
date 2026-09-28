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
            try
            {
                ActionDialog.Show(
                    Current?.MainWindow,
                    DialogKind.Warning,
                    "Something went wrong, but the tool is still running",
                    e.Exception.Message,
                    "Nothing on this machine was changed by this error.");
            }
            catch (System.Exception)
            {
                // If even the dialog cannot be shown, staying alive still beats terminating.
            }

            e.Handled = true;
        }
    }
}
