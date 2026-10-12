using System.Net.Sockets;
using System.ComponentModel;
using System.Text.Json;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.Nas;
using Jularr.MountHelper;

if (!OperatingSystem.IsLinux())
{
    throw new PlatformNotSupportedException("The mount helper requires Linux.");
}

// Listen on a private socket.
const string socketPath = "/run/jularr-mount-helper/control.sock";
if (File.Exists(socketPath)) File.Delete(socketPath);
using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
listener.Bind(new UnixDomainSocketEndPoint(socketPath));
File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
listener.Listen(16);
var mounts = new MountManager();

// Serialize mount transitions.
while (true)
{
    using var socket = await listener.AcceptAsync();
    using var stream = new NetworkStream(socket);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
    try
    {
        var request = await NasMountProtocol.ReadAsync<NasMountRequest>(stream, timeout.Token);
        var response = await mounts.ExecuteAsync(request, timeout.Token);
        await NasMountProtocol.WriteAsync(stream, response, timeout.Token);
    }
    catch (Exception exception) when (exception is IOException or JsonException or ArgumentException or OperationCanceledException or Win32Exception)
    {
        // Never log request credentials.
        Console.Error.WriteLine("Mount request failed.");
    }
}
