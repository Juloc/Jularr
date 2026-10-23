using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Acquisition.V1
{
    public sealed class SaveCustomFormatParametersV1 : BaseParameters
    {
    }

    public sealed class ReadCustomFormatsParametersV1 : BaseParameters
    {
        public ReadCustomFormatsParametersV1(MediaType mediaTypeId, int page = 1, int pageSize = 25)
        {
            MediaTypeId = mediaTypeId;
            Page = page;
            PageSize = pageSize;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        public int Page { get; }

        public int PageSize { get; }
    }
    public sealed class CustomFormatParametersV1 : BaseParameters
    {
        public CustomFormatParametersV1(long customFormatId)
        {
            CustomFormatId = customFormatId;
        }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.Id))]
        public long CustomFormatId { get; }
    }
    public sealed class DeleteCustomFormatParametersV1 : BaseParameters
    {
        public DeleteCustomFormatParametersV1(long customFormatId, long version)
        {
            CustomFormatId = customFormatId;
            Version = version;
        }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.Id))]
        public long CustomFormatId { get; }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.Version))]
        public long Version { get; }
    }
    public sealed class FormatConditionV1 : BaseData
    {
        public FormatConditionV1(string name, FormatConditionKind kind, bool negate, bool required, string? value, bool exceptLanguage, int? resolution, decimal? minimum, decimal? maximum)
        {
            Name = name;
            Kind = kind;
            Negate = negate;
            Required = required;
            Value = value;
            ExceptLanguage = exceptLanguage;
            Resolution = resolution;
            Minimum = minimum;
            Maximum = maximum;
        }

        [DataField(typeof(FormatConditionFields), nameof(FormatConditionFields.Name))]
        public string Name { get; }

        [DataField(typeof(FormatConditionFields), nameof(FormatConditionFields.Kind))]
        public FormatConditionKind Kind { get; }

        [DataField(typeof(FormatConditionFields), nameof(FormatConditionFields.Negate))]
        public bool Negate { get; }

        [DataField(typeof(FormatConditionFields), nameof(FormatConditionFields.Required))]
        public bool Required { get; }

        [DataField(typeof(FormatConditionFields), nameof(FormatConditionFields.Value))]
        public string? Value { get; }

        [DataField(typeof(FormatConditionFields), nameof(FormatConditionFields.ExceptLanguage))]
        public bool ExceptLanguage { get; }

        [DataField(typeof(FormatConditionFields), nameof(FormatConditionFields.Resolution))]
        public int? Resolution { get; }

        [DataField(typeof(FormatConditionFields), nameof(FormatConditionFields.Minimum))]
        public decimal? Minimum { get; }

        [DataField(typeof(FormatConditionFields), nameof(FormatConditionFields.Maximum))]
        public decimal? Maximum { get; }
    }
    public sealed class SaveCustomFormatDataV1 : BaseData
    {
        public SaveCustomFormatDataV1(long? id, MediaType mediaTypeId, string name, bool includeWhenRenaming, BaseDataList<FormatConditionV1> conditions, long? version)
        {
            Id = id;
            MediaTypeId = mediaTypeId;
            Name = name;
            IncludeWhenRenaming = includeWhenRenaming;
            Conditions = conditions;
            Version = version;
        }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.Id), false)]
        public long? Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.Name))]
        public string Name { get; }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.IncludeWhenRenaming))]
        public bool IncludeWhenRenaming { get; }

        public BaseDataList<FormatConditionV1> Conditions { get; }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.Version), false)]
        public long? Version { get; }
        public override void Validate()
        {
            base.Validate();
            if (Conditions is null || Conditions.Count is < 1 or > 64) throw new DataValidationException(nameof(Conditions), "out_of_range");
            Conditions.Validate(nameof(Conditions));
        }

    }
    public sealed class CustomFormatDetailsV1 : BaseResponse
    {
        public CustomFormatDetailsV1(long id, MediaType mediaTypeId, string name, bool includeWhenRenaming, BaseDataList<FormatConditionV1> conditions, long version)
        {
            Id = id;
            MediaTypeId = mediaTypeId;
            Name = name;
            IncludeWhenRenaming = includeWhenRenaming;
            Conditions = conditions;
            Version = version;
        }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.Name))]
        public string Name { get; }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.IncludeWhenRenaming))]
        public bool IncludeWhenRenaming { get; }

        public BaseDataList<FormatConditionV1> Conditions { get; }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.Version))]
        public long Version { get; }
        public override void Validate()
        {
            base.Validate();
            if (Conditions is null || Conditions.Count is < 1 or > 64) throw new DataValidationException(nameof(Conditions), "out_of_range");
            Conditions.Validate(nameof(Conditions));
        }

    }
    public sealed class SavedCustomFormatV1 : BaseResponse
    {
        public SavedCustomFormatV1(long id, long version)
        {
            Id = id;
            Version = version;
        }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.Id))]
        public long Id { get; }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.Version))]
        public long Version { get; }
    }
}
