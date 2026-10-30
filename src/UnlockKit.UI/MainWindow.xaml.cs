using System.Windows;
using UnlockKit.UI.Services;

namespace UnlockKit.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    /// <summary>The frame belongs to DWM, so it needs the dark hint once the HWND exists.</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Theme.Attach(this);
    }

    private void BrowseFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            CheckFileExists = true,
            Title = "Pick the locked file",
        };

        if (dialog.ShowDialog(this) == true)
            _vm.TargetPath = dialog.FileName;
    }

    private void BrowseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Pick the locked folder",
            ShowNewFolderButton = false,
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            _vm.TargetPath = dialog.SelectedPath;
    }
}
