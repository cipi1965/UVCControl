using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UVCControl.Core;

namespace UVCControl.App.ViewModels.Editors;

/// <summary>Hex editor for extension-unit controls, whose layout is unknown.</summary>
public sealed partial class RawEditorViewModel(ControlViewModel owner) : ControlEditorViewModel(owner)
{
    private string _syncedText = "";

    [ObservableProperty]
    public partial string HexText { get; set; } = "";

    [ObservableProperty]
    public partial string Details { get; private set; } = "";

    [ObservableProperty]
    public partial bool CanWrite { get; private set; }

    public override void Sync()
    {
        // Leave the text alone while the user is editing it.
        if (HexText == _syncedText) HexText = _syncedText = Control.Current.ToHexString();
        CanWrite = Control.SupportsSet;

        var lines = new List<string> { $"Length {Control.Length} bytes · info 0x{Control.Info:X2}" };
        if (Control.Current.PrintableAscii() is { } ascii) lines.Add($"ASCII: {ascii}");
        if (Control.Minimum is { } min) lines.Add($"Min {min.ToHexString()}");
        if (Control.Maximum is { } max) lines.Add($"Max {max.ToHexString()}");
        if (Control.DefaultValue is { } def) lines.Add($"Def {def.ToHexString()}");
        Details = string.Join(Environment.NewLine, lines);
    }

    [RelayCommand]
    private async Task ReadAsync()
    {
        await Owner.RunAsync(Control.ReadCurrentAsync);
        HexText = _syncedText = Control.Current.ToHexString();
    }

    [RelayCommand]
    private async Task WriteAsync()
    {
        if (ByteExtensions.ParseHex(HexText) is not { } bytes)
        {
            Owner.Error = "Not valid hex";
            return;
        }
        await Owner.RunAsync(() => Control.SendAsync(bytes.Padded(Control.Length)));
        HexText = _syncedText = Control.Current.ToHexString();
    }
}
