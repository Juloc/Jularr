using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Backend.Data.Logic.Library
{
    internal sealed class MediaPathInfo
    {
        public MediaPathInfo(string title, int? year, WorkUnitType? workUnitTypeId, decimal? unitNumber, decimal? parentUnitNumber)
        {
            Title = title;
            Year = year;
            WorkUnitTypeId = workUnitTypeId;
            UnitNumber = unitNumber;
            ParentUnitNumber = parentUnitNumber;
        }

        public string Title { get; }
        public int? Year { get; }
        public WorkUnitType? WorkUnitTypeId { get; }
        public decimal? UnitNumber { get; }
        public decimal? ParentUnitNumber { get; }
    }
}
