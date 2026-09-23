using Avalonia.Controls;
using UVCControl.App.ViewModels;

namespace UVCControl.App.Views;

public partial class MainWindow : Window
{
    /// <summary>Used by the XAML previewer.</summary>
    public MainWindow() => InitializeComponent();

    public MainWindow(MainWindowViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Opened += async (_, _) => await viewModel.RescanCommand.ExecuteAsync(null);
        Closing += (_, _) => viewModel.SelectedDevice?.Deactivate();
    }
}
