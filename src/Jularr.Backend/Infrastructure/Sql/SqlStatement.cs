using Jularr.Backend.Data;

namespace Jularr.Backend.Infrastructure.Sql
{
    public sealed class SqlStatement
    {
        public string Statement { get; set; } = string.Empty;
        public List<object> Parameters { get; } = [];
        public PageRequest? Page { get; set; }
    }
}
