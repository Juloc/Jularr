namespace Jularr.Integrations.Data.Database
{
    // Where the PostgreSQL tools connect; the password travels in their environment, never as an argument.
    internal sealed class PostgresConnection(string host, int port, string database, string username, string? password)
    {
        public string Host { get; } = host;
        public int Port { get; } = port;
        public string Database { get; } = database;
        public string Username { get; } = username;
        public string? Password { get; } = password;
    }
}
