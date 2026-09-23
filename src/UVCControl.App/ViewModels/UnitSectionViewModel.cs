using UVCControl.Core.Devices;

namespace UVCControl.App.ViewModels;

public sealed class UnitSectionViewModel(UnitSection section) : ViewModelBase
{
    public string Header => $"{section.Title} · ID {section.UnitId}";

    public string? Guid => section.ExtensionGuid?.ToString().ToUpperInvariant();

    public IReadOnlyList<ControlViewModel> Controls { get; } = section.Controls.Select(c => new ControlViewModel(c)).ToList();

    public bool IsEmpty => Controls.Count == 0;
}
