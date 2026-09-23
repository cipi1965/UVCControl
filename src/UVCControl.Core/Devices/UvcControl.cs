using Microsoft.Extensions.Logging;
using UVCControl.Core.Controls;
using UVCControl.Core.Transport;

namespace UVCControl.Core.Devices;

/// <summary>One control on one unit, together with its last known values.</summary>
/// <remarks>
/// State is written on the owning device's I/O thread; byte arrays are replaced, never mutated,
/// so readers on other threads always see a consistent payload.
/// </remarks>
public sealed class UvcControl
{
    private volatile bool _isEditing;

    internal UvcControl(UvcDevice device, byte unitId, UnitKind unitKind, ControlDefinition definition, bool advertised)
    {
        Device = device;
        UnitId = unitId;
        UnitKind = unitKind;
        Definition = definition;
        Advertised = advertised;
        Length = definition.Length;
        Current = new byte[definition.Length];
        Id = $"{unitId}-{definition.Selector}";
    }

    public string Id { get; }
    public UvcDevice Device { get; }
    public byte UnitId { get; }
    public UnitKind UnitKind { get; }
    public ControlDefinition Definition { get; }

    /// <summary>
    /// Whether the unit descriptor's bmControls lists this control. Some devices
    /// (e.g. DJI Osmo Pocket) answer requests for controls they don't advertise.
    /// </summary>
    public bool Advertised { get; }

    public byte Info { get; private set; }
    public int Length { get; private set; }
    public byte[]? Minimum { get; private set; }
    public byte[]? Maximum { get; private set; }
    public byte[]? Resolution { get; private set; }
    public byte[]? DefaultValue { get; private set; }
    public byte[] Current { get; private set; }
    public string? LastError { get; internal set; }

    /// <summary>While true, refreshes leave <see cref="Current"/> alone so a slider being dragged isn't overwritten.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set => _isEditing = value;
    }

    public bool SupportsGet => (Info & 0x01) != 0;
    public bool SupportsSet => (Info & 0x02) != 0;
    public bool DisabledByAutoMode => (Info & 0x04) != 0;
    public bool AutoUpdates => (Info & 0x08) != 0;
    public bool IsAsynchronous => (Info & 0x10) != 0;

    // MARK: Field access

    public long Value(ControlField field, byte[]? buffer = null) =>
        (buffer ?? Current).ReadInt(field.Offset, field.Size, field.Signed);

    public long? MinValue(ControlField field) => Minimum is { } b ? Value(field, b) : null;
    public long? MaxValue(ControlField field) => Maximum is { } b ? Value(field, b) : null;
    public long? ResValue(ControlField field) => Resolution is { } b ? Value(field, b) : null;
    public long? DefValue(ControlField field) => DefaultValue is { } b ? Value(field, b) : null;

    // MARK: Commands (all run on the device's I/O thread)

    /// <summary>Updates one field and sends the full payload with SET_CUR.</summary>
    public Task SetAsync(ControlField field, long value) =>
        Device.Executor.InvokeAsync(() => SendNow(Current.WithInt(value, field.Offset, field.Size), updateCurrent: true));

    /// <summary>Sends a payload with SET_CUR. <see cref="Current"/> is updated optimistically.</summary>
    public Task SendAsync(byte[] payload, bool updateCurrent = true) =>
        Device.Executor.InvokeAsync(() => SendNow(payload, updateCurrent));

    public Task ResetToDefaultAsync() => Device.Executor.InvokeAsync(ResetToDefaultNow);

    public Task ReadCurrentAsync() => Device.Executor.InvokeAsync(() => ReadCurrent(force: true));

    internal void ResetToDefaultNow()
    {
        if (DefaultValue is { } def) SendNow(def, updateCurrent: true);
    }

    private void SendNow(byte[] payload, bool updateCurrent)
    {
        Device.Logger.LogDebug("SET_CUR unit {Unit} selector {Selector}: {Payload}", UnitId, Definition.Selector, payload.ToHexString());
        try
        {
            Device.RequireTransport().Set(UnitId, Definition.Selector, payload);
            if (updateCurrent) Current = payload;
            LastError = null;
        }
        catch (UvcException e)
        {
            LastError = $"SET_CUR: {e.Message}";
        }
        Device.ScheduleRefresh();
    }

    // MARK: Device I/O

    /// <summary>Queries GET_INFO and the ranges. Returns false when the device doesn't implement the control.</summary>
    internal bool Probe(IUvcTransport transport)
    {
        // Every real control supports GET. DJI cameras answer unimplemented units with junk
        // like 0x06 (SET without GET), so require the GET bit.
        if (!TryGet(transport, UvcRequest.GetInfo, 1, out var info) || info.Length == 0 || (info[0] & 0x01) == 0)
            return false;
        Info = info[0];

        if (UnitKind == UnitKind.ExtensionUnit)
        {
            if (!TryGet(transport, UvcRequest.GetLen, 2, out var len) || len.Length != 2) return false;
            Length = (int)len.ReadInt(0, 2, signed: false);
            Current = new byte[Length];
        }
        if (Length <= 0) return false;

        Minimum = TryGet(transport, UvcRequest.GetMin, Length, out var min) ? min.Padded(Length) : null;
        Maximum = TryGet(transport, UvcRequest.GetMax, Length, out var max) ? max.Padded(Length) : null;
        Resolution = TryGet(transport, UvcRequest.GetRes, Length, out var res) ? res.Padded(Length) : null;
        DefaultValue = TryGet(transport, UvcRequest.GetDef, Length, out var def) ? def.Padded(Length) : null;
        ReadCurrent(force: false);
        return true;
    }

    internal void ReadCurrent(bool force)
    {
        var transport = Device.Transport;
        if (transport is null) return;
        if (TryGet(transport, UvcRequest.GetInfo, 1, out var info) && info.Length > 0) Info = info[0];
        if (!SupportsGet || (IsEditing && !force)) return;
        try
        {
            Current = transport.Get(UvcRequest.GetCur, UnitId, Definition.Selector, Length).Padded(Length);
        }
        catch (UvcException e)
        {
            LastError = $"GET_CUR: {e.Message}";
        }
    }

    private bool TryGet(IUvcTransport transport, UvcRequest request, int length, out byte[] result)
    {
        try
        {
            result = transport.Get(request, UnitId, Definition.Selector, length);
            return true;
        }
        catch (UvcException)
        {
            result = [];
            return false;
        }
    }
}
