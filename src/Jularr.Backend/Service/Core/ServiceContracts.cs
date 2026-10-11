using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Common;

namespace Jularr.Backend.Service.Core
{
    public sealed record ModuleRequirement
    {
        public static ModuleRequirement None { get; } = new(ModuleRequirementMode.None, []);

        public ModuleRequirementMode Mode { get; }
        public IReadOnlyList<string> ModuleKeys { get; }

        private ModuleRequirement(ModuleRequirementMode mode, IReadOnlyList<string> moduleKeys)
        {
            Mode = mode;
            ModuleKeys = moduleKeys;
        }

        public static ModuleRequirement All(params string[] moduleKeys) => Create(ModuleRequirementMode.All, moduleKeys);

        public static ModuleRequirement Any(params string[] moduleKeys) => Create(ModuleRequirementMode.Any, moduleKeys);

        private static ModuleRequirement Create(ModuleRequirementMode mode, string[] moduleKeys)
        {
            ArgumentNullException.ThrowIfNull(moduleKeys);

            if (moduleKeys.Length == 0 || moduleKeys.Any(string.IsNullOrWhiteSpace) || moduleKeys.Distinct(StringComparer.Ordinal).Count() != moduleKeys.Length)
            {
                throw new ArgumentException("Modules must contain distinct, non-empty keys.", nameof(moduleKeys));
            }

            return new ModuleRequirement(mode, Array.AsReadOnly((string[])moduleKeys.Clone()));
        }
    }

    public sealed record ServiceResultType
    {
        public Type Type { get; }
        public bool IsDefault { get; }

        private ServiceResultType(Type type, bool isDefault)
        {
            Type = type;
            IsDefault = isDefault;
        }

        public static ServiceResultType Default<T>() where T : IServiceOutput => new(typeof(T), true);

        public static ServiceResultType Additional<T>() where T : IServiceOutput => new(typeof(T), false);
    }

    public sealed record ServicePermission
    {
        public static ServicePermission Public { get; } = new("public");
        public string Key { get; }

        public ServicePermission(string key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            Key = key;
        }
    }

    public sealed record ServiceCaller(
        long? AccountId,
        long? ProfileId,
        ServiceArea Area,
        string PrincipalKey,
        Guid? SessionPublicId = null,
        long? SessionRevision = null,
        long? ProfileOwnerAccountId = null,
        byte[]? SessionTokenHash = null);

    public interface IServiceDefinition
    {
        void ValidateDefinition();
        ServiceOperationType OperationType { get; }
        Type DefaultResultType { get; }
        ServiceArea ServiceArea { get; }
    }
}
