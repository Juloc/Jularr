using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    // One downloaded file and where it belongs; files of one group share the final name.
    internal sealed class ImportPlacementData
    {
        public ImportPlacementData(int group, string sourcePath, string targetStem, string extension, bool isMedia, WorkUnitType? workUnitTypeId, decimal? unitNumber,
            decimal? parentUnitNumber)
        {
            Group = group;
            SourcePath = sourcePath;
            TargetStem = targetStem;
            Extension = extension;
            IsMedia = isMedia;
            WorkUnitTypeId = workUnitTypeId;
            UnitNumber = unitNumber;
            ParentUnitNumber = parentUnitNumber;
        }

        public int Group { get; }
        public string SourcePath { get; }
        public string TargetStem { get; }
        public string Extension { get; }
        public bool IsMedia { get; }
        public WorkUnitType? WorkUnitTypeId { get; }
        public decimal? UnitNumber { get; }
        public decimal? ParentUnitNumber { get; }
    }
}
