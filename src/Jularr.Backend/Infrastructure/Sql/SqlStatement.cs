using Jularr.Backend.Data;

namespace Jularr.Backend.Infrastructure.Sql
{
    internal sealed class SqlStatement
    {
        public string Statement { get; set; } = string.Empty;
        public List<object> Parameters { get; } = [];
        public PageRequest? Page { get; set; }

        // Avoid compilation overhead for short reads; scoped to the operation's transaction.
        public bool DisableJit { get; set; }
    }
}
