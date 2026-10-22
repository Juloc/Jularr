using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Data.Fields.Instance;

namespace Jularr.Backend.Data.Admin.System.V1
{
    public sealed class ReadHealthParametersV1 : BaseParameters
    {
    }

    public sealed class CheckHealthParametersV1 : BaseParameters
    {
    }

    // The result of the last health check: every configured service and whether all of them are fine.
    public sealed class HealthV1 : BaseResponse
    {
        public HealthV1(bool isStable, DateTime? checkedAt, BaseDataList<HealthServiceV1> services)
        {
            IsStable = isStable;
            CheckedAt = checkedAt;
            Services = services;
        }

        // False as soon as one service did not answer; a service nothing has asked yet does not count.
        [DataField(typeof(HealthFields), nameof(HealthFields.IsStable))]
        public bool IsStable { get; }

        [DataField(typeof(HealthFields), nameof(HealthFields.CheckedAt))]
        public DateTime? CheckedAt { get; }

        public BaseDataList<HealthServiceV1> Services { get; }
    }

    // One checked service. A name tells several of a kind apart; the error key names why it did not answer.
    public sealed class HealthServiceV1 : BaseResponse
    {
        public HealthServiceV1(HealthServiceType healthServiceTypeId, string key, string? name, HealthStateType healthStateTypeId, string? errorKey, DateTime? checkedAt)
        {
            HealthServiceTypeId = healthServiceTypeId;
            Key = key;
            Name = name;
            HealthStateTypeId = healthStateTypeId;
            ErrorKey = errorKey;
            CheckedAt = checkedAt;
        }

        [DataField(typeof(HealthFields), nameof(HealthFields.ServiceTypeId))]
        public HealthServiceType HealthServiceTypeId { get; }

        [DataField(typeof(HealthFields), nameof(HealthFields.Key))]
        public string Key { get; }

        [DataField(typeof(HealthFields), nameof(HealthFields.Name))]
        public string? Name { get; }

        [DataField(typeof(HealthFields), nameof(HealthFields.StateTypeId))]
        public HealthStateType HealthStateTypeId { get; }

        [DataField(typeof(HealthFields), nameof(HealthFields.ErrorKey))]
        public string? ErrorKey { get; }

        [DataField(typeof(HealthFields), nameof(HealthFields.CheckedAt))]
        public DateTime? CheckedAt { get; }
    }
}
