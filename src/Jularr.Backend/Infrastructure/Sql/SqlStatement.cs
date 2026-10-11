namespace Jularr.Backend.Infrastructure.Sql
{
    public sealed class SqlStatement
    {
        public string Statement { get; set; } = string.Empty;
        public List<object> Parameters { get; } = [];
    }
}
