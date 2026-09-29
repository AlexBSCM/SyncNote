using System.IO;
using System.Windows;

namespace SyncNote.Windows.Views;

public partial class PairingWindow : Window
{
    public PairingWindow(string dbPath, string deviceId)
    {
        InitializeComponent();
        DataContext = new ViewModels.PairingViewModel(dbPath, deviceId);
        if (DataContext is ViewModels.PairingViewModel vm)
            vm.RequestClose += (_, _) => Close();
    }
}