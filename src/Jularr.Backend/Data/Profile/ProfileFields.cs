using Jularr.Data.Common;
using NpgsqlTypes;

namespace Jularr.Data.Profile;

public static class ProfileFields
{
    public static readonly DataField<long> Id = new("Profiles", "Id", NpgsqlDbType.Bigint);
    public static readonly DataField<Guid> PublicId = new("Profiles", "PublicId", NpgsqlDbType.Uuid);
    public static readonly DataField<string> DisplayName = new("Profiles", "DisplayName", NpgsqlDbType.Text);
}
