namespace Jularr.Web.Features.Instance;

public enum InstancePreset
{
    /// <summary>Any combination of switches that is neither of the named presets.</summary>
    Custom,

    /// <summary>Discover, request, monitor and acquire media without playing it: playback (and with it offline copies and optimization), learning and tracking are off, acquisition is on, and the media types stay as the owner chose them.</summary>
    MediaManager,

    /// <summary>Every module is on.</summary>
    Full
}

/// <summary>
/// Named combinations of the instance module switches. A preset is only a way to set the one canonical switch store
/// (<see cref="IInstanceModuleService"/>); it adds no runtime of its own, and a disabled module is gated by that store exactly as if the owner had
/// switched it off by hand. Which media types a Media Manager serves is the owner's choice, so the preset leaves them as they are.
/// </summary>
public static class InstanceModulePresets
{
    private static readonly InstanceModule[] ManagerOff = [InstanceModule.Playback, InstanceModule.Learning, InstanceModule.Tracking];

    public static InstanceModuleSettings Apply(InstancePreset preset, InstanceModuleSettings current) =>
        preset switch
        {
            InstancePreset.MediaManager => ManagerOff.Aggregate(current.With(InstanceModule.Acquisition, true), (settings, module) => settings.With(module, false)),
            InstancePreset.Full => Enum.GetValues<InstanceModule>().Aggregate(current, (settings, module) => settings.With(module, true)),
            _ => current
        };

    public static InstancePreset Detect(InstanceModuleSettings settings)
    {
        if (Enum.GetValues<InstanceModule>().All(settings.IsEnabled))
        {
            return InstancePreset.Full;
        }

        var servesMedia = Enum.GetValues<InstanceModule>().Except([InstanceModule.Learning, InstanceModule.Acquisition, InstanceModule.Tracking, InstanceModule.Playback]).Any(settings.IsEnabled);
        return settings.IsEnabled(InstanceModule.Acquisition) && servesMedia && ManagerOff.All(module => !settings.IsEnabled(module))
            ? InstancePreset.MediaManager
            : InstancePreset.Custom;
    }
}
