using System;
using System.Windows;
using System.Windows.Media.Imaging;
using WinUpgradeDiag.App.ViewModels;

namespace WinUpgradeDiag.App
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
            ApplyIcon();
        }

        /// <summary>
        /// Sets the window icon defensively. Declaring it in XAML makes the icon a hard dependency
        /// of constructing the window: if the resource cannot be resolved, the whole application
        /// fails to start. A decoration must never be able to do that, so it is loaded here and a
        /// failure simply leaves the default icon in place.
        /// </summary>
        private void ApplyIcon()
        {
            try
            {
                Icon = BitmapFrame.Create(
                    new Uri("pack://application:,,,/WinUpgradeDiag;component/app.ico", UriKind.Absolute));
            }
            catch (Exception)
            {
                // No icon is a cosmetic problem; a window that will not open is not.
            }
        }
    }
}
