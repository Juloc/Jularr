namespace Jularr.Web.Features.Acquisition.Wanted;

public enum WantedTargetKind : short
{
    Work = 0,
    Episode = 1,
    Volume = 2,
    Chapter = 3,
    Edition = 4,
    Recording = 5,
    ReleaseTrack = 6
}

// One concrete target the library should still acquire; why (Request, Monitoring) and whether it is missing or an upgrade are derived on read.
public sealed class WantedItem
{
    public long Id { get; set; }

    public long WorkId { get; set; }

    public WantedTargetKind TargetKind { get; set; }

    // The node of the target; null for a Work target, which is the row's own Work.
    public Guid? TargetId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
