using System.Globalization;
using System.Net.NetworkInformation;
using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Admin;

/// <summary>One cgroup reading. CPU is cumulative microseconds; memory is the current cgroup charge.</summary>
public sealed record CgroupResourceUsage(
    long CpuUsageMicroseconds,
    long MemoryBytes,
    long? ReceiveBytes = null,
    long? SendBytes = null,
    long? ReadBytes = null,
    long? WriteBytes = null);

/// <summary>One service's current resource use. A null CPU means that a rate baseline is still being established.</summary>
public sealed record StackServiceResource(
    double? CpuPercent,
    long MemoryBytes,
    double? ReceiveBytesPerSecond = null,
    double? SendBytesPerSecond = null,
    double? ReadBytesPerSecond = null,
    double? WriteBytesPerSecond = null);

/// <summary>
/// One reading for the only two containers Jularr owns: its complete service cgroup and PostgreSQL's
/// cgroup. No host totals, network counters or unrelated containers are represented here.
/// </summary>
public sealed record StackResourceSample(
    DateTimeOffset AtUtc,
    StackServiceResource? Jularr,
    StackServiceResource? PostgreSql)
{
    public double? TotalCpuPercent => Jularr?.CpuPercent is { } web && PostgreSql?.CpuPercent is { } database
        ? web + database
        : null;

    public long? TotalMemoryBytes => Jularr is { } web && PostgreSql is { } database
        ? web.MemoryBytes + database.MemoryBytes
        : null;

    public bool IsComplete => Jularr is not null && PostgreSql is not null;
}

/// <summary>The newest stack readings, oldest first. History deliberately resets with the app process.</summary>
public sealed record StackResourceSnapshot(IReadOnlyList<StackResourceSample> History)
{
    public static readonly StackResourceSnapshot Empty = new([]);

    public StackResourceSample? Current => History.Count == 0 ? null : History[^1];
}

public interface IStackResourceTelemetry
{
    StackResourceSnapshot GetSnapshot();
}

/// <summary>Reads exactly the cgroup values used by the two existing Compose services.</summary>
public interface IStackResourceSource
{
    Task<CgroupResourceUsage?> ReadJularrAsync(CancellationToken cancellationToken);

    Task<CgroupResourceUsage?> ReadPostgreSqlAsync(CancellationToken cancellationToken);
}

/// <summary>Parsers for cgroup v2's documented cpu.stat and memory.current files.</summary>
public static class CgroupV2Parser
{
    public static long? ParseCpuUsageMicroseconds(string? cpuStat)
    {
        if (string.IsNullOrWhiteSpace(cpuStat))
        {
            return null;
        }

        foreach (var line in cpuStat.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[0] == "usage_usec" &&
                long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var usage) && usage >= 0)
            {
                return usage;
            }
        }

        return null;
    }

    public static long? ParseMemoryBytes(string? memoryCurrent) =>
        long.TryParse(memoryCurrent?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) && bytes >= 0
            ? bytes
            : null;

    /// <summary>Totals the readable and written bytes from cgroup v2's <c>io.stat</c> device rows.</summary>
    public static CgroupIoBytes? ParseIoBytes(string? ioStat)
    {
        if (string.IsNullOrWhiteSpace(ioStat))
        {
            return null;
        }

        long read = 0;
        long written = 0;
        var found = false;
        foreach (var line in ioStat.Split('\n'))
        {
            foreach (var field in line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1))
            {
                var separator = field.IndexOf('=');
                if (separator <= 0 || !long.TryParse(field[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 0)
                {
                    continue;
                }

                if (field[..separator] == "rbytes")
                {
                    read += value;
                    found = true;
                }
                else if (field[..separator] == "wbytes")
                {
                    written += value;
                    found = true;
                }
            }
        }

        return found ? new CgroupIoBytes(read, written) : null;
    }
}

public sealed record CgroupIoBytes(long ReadBytes, long WriteBytes);

/// <summary>
/// The production source. Jularr reads its own cgroup directly. PostgreSQL reads the same fixed
/// paths inside its own container over the already-authenticated local database connection. Neither
/// operation accepts a path from a request or configuration.
/// </summary>
public sealed class CgroupStackResourceSource(IServiceScopeFactory scopes, ILogger<CgroupStackResourceSource> logger)
    : IStackResourceSource
{
    private const string CpuStatPath = "/sys/fs/cgroup/cpu.stat";
    private const string MemoryCurrentPath = "/sys/fs/cgroup/memory.current";
    private const string IoStatPath = "/sys/fs/cgroup/io.stat";

    public Task<CgroupResourceUsage?> ReadJularrAsync(CancellationToken cancellationToken)
    {
        if (!IsContainerizedLinux())
        {
            return Task.FromResult<CgroupResourceUsage?>(null);
        }

        var usage = ReadFiles(File.ReadAllText);
        var network = ReadContainerNetworkTotals();
        return Task.FromResult(usage is null ? null : usage with { ReceiveBytes = network?.Received, SendBytes = network?.Sent });
    }

    public async Task<CgroupResourceUsage?> ReadPostgreSqlAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cpu = await ReadPostgresFileAsync(db, CpuStatPath, cancellationToken);
            var memory = await ReadPostgresFileAsync(db, MemoryCurrentPath, cancellationToken);
            var io = await ReadPostgresFileAsync(db, IoStatPath, cancellationToken);
            return FromTexts(cpu, memory, io);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException
            or Npgsql.PostgresException)
        {
            logger.LogDebug(exception, "PostgreSQL cgroup telemetry is unavailable.");
            return null;
        }
    }

    private static CgroupResourceUsage? ReadFiles(Func<string, string> read)
    {
        try
        {
            return FromTexts(read(CpuStatPath), read(MemoryCurrentPath), read(IoStatPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static bool IsContainerizedLinux() => OperatingSystem.IsLinux() &&
        (File.Exists("/.dockerenv") || File.Exists("/run/.containerenv"));

    private static CgroupResourceUsage? FromTexts(string? cpuStat, string? memoryCurrent, string? ioStat) =>
        CgroupV2Parser.ParseCpuUsageMicroseconds(cpuStat) is { } cpu && CgroupV2Parser.ParseMemoryBytes(memoryCurrent) is { } memory
            ? CgroupV2Parser.ParseIoBytes(ioStat) is { } io
                ? new CgroupResourceUsage(cpu, memory, ReadBytes: io.ReadBytes, WriteBytes: io.WriteBytes)
                : new CgroupResourceUsage(cpu, memory)
            : null;

    private static async Task<string?> ReadPostgresFileAsync(AppDbContext db, string path, CancellationToken cancellationToken)
    {
        // This path is a private constant. The database is never asked to read a caller-controlled path.
        return await db.Database.SqlQuery<string>(
                $"SELECT pg_read_file({path}, true) AS \"Value\"")
            .SingleOrDefaultAsync(cancellationToken);
    }

    // This runs inside the Jularr container namespace, so these counters do not include host traffic
    // or any sibling container. PostgreSQL deliberately has network_mode: none in the canonical stack.
    private static (long Received, long Sent)? ReadContainerNetworkTotals()
    {
        try
        {
            long received = 0;
            long sent = 0;
            var any = false;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var statistics = nic.GetIPStatistics();
                received += statistics.BytesReceived;
                sent += statistics.BytesSent;
                any = true;
            }

            return any ? (received, sent) : null;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }
}

/// <summary>Samples only Jularr and PostgreSQL resource cgroups into the Admin dashboard's bounded history.</summary>
public sealed class StackResourceTelemetrySampler(
    IStackResourceSource source,
    TimeProvider clock,
    ILogger<StackResourceTelemetrySampler> logger)
    : BackgroundService, IStackResourceTelemetry
{
    public const int Capacity = 60;
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly Lock gate = new();
    private readonly Queue<StackResourceSample> samples = new(Capacity);
    private DateTimeOffset? previousAt;
    private CgroupResourceUsage? previousJularr;
    private CgroupResourceUsage? previousPostgreSql;

    public StackResourceSnapshot GetSnapshot()
    {
        lock (gate)
        {
            return new StackResourceSnapshot(samples.ToArray());
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                Record(await SampleAsync(stoppingToken));
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Stack resource telemetry stopped unexpectedly.");
        }
    }

    public async Task<StackResourceSample> SampleAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var readings = await Task.WhenAll(
            source.ReadJularrAsync(cancellationToken),
            source.ReadPostgreSqlAsync(cancellationToken));
        var elapsed = previousAt is { } previous ? now - previous : TimeSpan.Zero;
        var sample = new StackResourceSample(
            now,
            ToService(readings[0], previousJularr, elapsed),
            ToService(readings[1], previousPostgreSql, elapsed));

        previousAt = now;
        previousJularr = readings[0];
        previousPostgreSql = readings[1];
        return sample;
    }

    private static StackServiceResource? ToService(CgroupResourceUsage? current, CgroupResourceUsage? previous, TimeSpan elapsed)
    {
        if (current is null)
        {
            return null;
        }

        double? cpu = null;
        if (previous is not null && elapsed > TimeSpan.Zero && current.CpuUsageMicroseconds >= previous.CpuUsageMicroseconds)
        {
            cpu = (current.CpuUsageMicroseconds - previous.CpuUsageMicroseconds) * 100d / elapsed.TotalMicroseconds;
        }

        var receive = Rate(previous?.ReceiveBytes, current.ReceiveBytes, elapsed);
        var send = Rate(previous?.SendBytes, current.SendBytes, elapsed);
        var read = Rate(previous?.ReadBytes, current.ReadBytes, elapsed);
        var written = Rate(previous?.WriteBytes, current.WriteBytes, elapsed);
        return new StackServiceResource(cpu, current.MemoryBytes, receive, send, read, written);
    }

    private static double? Rate(long? previous, long? current, TimeSpan elapsed) =>
        previous is { } before && current is { } now && now >= before && elapsed > TimeSpan.Zero
            ? (now - before) / elapsed.TotalSeconds
            : null;

    private void Record(StackResourceSample sample)
    {
        lock (gate)
        {
            while (samples.Count >= Capacity)
            {
                samples.Dequeue();
            }

            samples.Enqueue(sample);
        }
    }
}
