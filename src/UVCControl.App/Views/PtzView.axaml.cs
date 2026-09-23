using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace UVCControl.App.Views;

public partial class PtzView : UserControl
{
    public PtzView() => InitializeComponent();

    /// <summary>Focuses and selects the rename box when it appears, and commits the rename when it loses focus.</summary>
    private void OnRenameBoxAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not TextBox box) return;
        box.PropertyChanged += (_, args) =>
        {
            if (args.Property != IsVisibleProperty || !box.IsVisible) return;
            Dispatcher.UIThread.Post(() =>
            {
                box.Focus();
                box.SelectAll();
            });
        };
        box.LostFocus += (_, _) =>
        {
            if (box.DataContext is ViewModels.PresetItemViewModel { IsRenaming: true } item) item.CommitRenameCommand.Execute(null);
        };
    }
}
