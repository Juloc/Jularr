namespace Jularr.Backend.Data.Enums.Sql
{
    // The connection pool a service draws on: web requests and background work never wait for each other's connections.
    public enum SqlLane : byte
    {
        Web,
        Worker
    }
}
