using System.Windows;
namespace DevHarbor.Desktop;
public partial class MainWindow : Window
{
    public MainWindow() : this(new StorageViewModel()) { }
    public MainWindow(StorageViewModel model)
    {
        InitializeComponent(); DataContext = model;
        Closing += (_, _) => model.Cancel();
    }
}
