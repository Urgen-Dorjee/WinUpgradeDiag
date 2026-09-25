using System.Windows;
using WinUpgradeDiag.App.ViewModels;

namespace WinUpgradeDiag.App
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
        }
    }
}
