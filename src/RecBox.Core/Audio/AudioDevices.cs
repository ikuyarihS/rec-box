using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace RecBox.Core.Audio;

public enum SourceKind
{
    Mic,
    Speaker,
}

public sealed record AudioDevice(string Id, string Name, bool IsDefault);

public static class AudioDevices
{
    public static DataFlow FlowOf(SourceKind kind) => kind == SourceKind.Mic ? DataFlow.Capture : DataFlow.Render;
    public static Role RoleOf(SourceKind kind) => kind == SourceKind.Mic ? Role.Console : Role.Multimedia;

    public static IReadOnlyList<AudioDevice> List(SourceKind kind)
    {
        var result = new List<AudioDevice>();
        try
        {
            using var en = new MMDeviceEnumerator();
            var defId = DefaultId(en, kind);
            foreach (var d in en.EnumerateAudioEndPoints(FlowOf(kind), DeviceState.Active))
            {
                using (d)
                {
                    result.Add(new AudioDevice(d.ID, d.FriendlyName, d.ID == defId));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"listing {kind} devices failed", ex);
        }
        return result;
    }

    public static string? DefaultId(SourceKind kind)
    {
        using var en = new MMDeviceEnumerator();
        return DefaultId(en, kind);
    }

    private static string? DefaultId(MMDeviceEnumerator en, SourceKind kind)
    {
        try
        {
            if (!en.HasDefaultAudioEndpoint(FlowOf(kind), RoleOf(kind))) return null;
            using var d = en.GetDefaultAudioEndpoint(FlowOf(kind), RoleOf(kind));
            return d.ID;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Opens the endpoint by id, or the current default when id is null. Null when unavailable.</summary>
    public static MMDevice? Open(SourceKind kind, string? id)
    {
        try
        {
            var en = new MMDeviceEnumerator();
            if (id is null)
            {
                return en.HasDefaultAudioEndpoint(FlowOf(kind), RoleOf(kind))
                    ? en.GetDefaultAudioEndpoint(FlowOf(kind), RoleOf(kind))
                    : null;
            }
            var d = en.GetDevice(id);
            return d.State == DeviceState.Active ? d : null;
        }
        catch (Exception ex)
        {
            Log.Error($"opening {kind} device {id ?? "(default)"} failed", ex);
            return null;
        }
    }
}

/// <summary>
/// Raises events when Windows changes the default mic or speaker, or a device goes away.
/// Callbacks arrive on a COM thread; handlers are invoked on the thread pool so they may touch audio APIs.
/// </summary>
public sealed class DeviceWatcher : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private bool _registered;

    public event Action<SourceKind, string>? DefaultChanged;
    public event Action<string>? DeviceLost;

    public DeviceWatcher()
    {
        try
        {
            _enumerator.RegisterEndpointNotificationCallback(this);
            _registered = true;
        }
        catch (Exception ex)
        {
            Log.Error("device notifications unavailable", ex);
        }
    }

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        SourceKind? kind = (flow, role) switch
        {
            (DataFlow.Capture, Role.Console) => SourceKind.Mic,
            (DataFlow.Render, Role.Multimedia) => SourceKind.Speaker,
            _ => null,
        };
        if (kind is { } k && !string.IsNullOrEmpty(defaultDeviceId))
        {
            Task.Run(() => DefaultChanged?.Invoke(k, defaultDeviceId));
        }
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        if (newState != DeviceState.Active) Task.Run(() => DeviceLost?.Invoke(deviceId));
    }

    public void OnDeviceRemoved(string deviceId) => Task.Run(() => DeviceLost?.Invoke(deviceId));
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        if (_registered)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
            _registered = false;
        }
        _enumerator.Dispose();
    }
}
