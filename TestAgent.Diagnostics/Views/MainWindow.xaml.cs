using System.Windows;
using TestAgent.Diagnostics.ViewModels;

namespace TestAgent.Diagnostics.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}
