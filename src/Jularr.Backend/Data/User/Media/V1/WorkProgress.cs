using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Playback;

namespace Jularr.Backend.Data.User.Media.V1
{
    // What the profile marks: a film or a whole series (no unit), or a season, an episode, a volume or a track with everything below it.
    public sealed class UpdateWorkProgressDataV1 : BaseData
    {
        public UpdateWorkProgressDataV1(ProgressAction action, long? workUnitId = null)
        {
            Action = action;
            WorkUnitId = workUnitId;
        }

        [DataField(typeof(ProgressFields), nameof(ProgressFields.Action))]
        public ProgressAction Action { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.Id), false)]
        public long? WorkUnitId { get; }
    }
}
