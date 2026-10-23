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

    // The reason in Jularr's words; the message keeps the end of the tool's error output for the log.
    internal sealed class PostgresToolException(string code, string output) : Exception(output.Length > 0 ? "The PostgreSQL tool failed: " + output : "The PostgreSQL tool failed.")
    {
        public string Code { get; } = code;
    }
}
