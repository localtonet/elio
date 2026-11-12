using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Elio.Connections;
using Elio.Core;

namespace Elio.Localtonet;

/// <summary>
/// The path to a device for one session. Localtonet's node joins this connection with the device's own,
/// and only passes the bytes along: the SSH inside is between this app and the device, and the device's
/// key is checked against the one Localtonet recorded when it was paired.
/// </summary>
/// <remarks>
/// The SSH library dials its own sockets, so it connects to a private local port that leads here.
/// The port takes one connection and closes, and is gone when the session ends.
/// </remarks>
public sealed class LocaltonetLink : IAsyncDisposable
{
    private const string Prefix = "LT!";
    private const char ClientRole = 'C';
    private const string DeviceUser = "device";
    private const int CopyBufferSize = 64 * 1024;
    private static readonly TimeSpan ArrivalWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan LocalAcceptWindow = TimeSpan.FromSeconds(30);

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _closed = new();
    private readonly string _pinnedFingerprint;

    private LocaltonetLink(TcpListener listener, DeviceSession session)
    {
        _listener = listener;
        _pinnedFingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(session.HostKeyBlob))).TrimEnd('=');
        Endpoint = new SshEndpoint(IPAddress.Loopback.ToString(), ((IPEndPoint)listener.LocalEndpoint).Port, DeviceUser, session.Ticket);
    }

    /// <summary>What to hand the SSH connection.</summary>
    public SshEndpoint Endpoint { get; }

    /// <summary>Accepts only the key Localtonet recorded for this device.</summary>
    public bool IsDeviceKey(SshEndpoint endpoint, HostKeyInfo key) => endpoint == Endpoint && key.Fingerprint == _pinnedFingerprint;

    /// <summary>Reaches the device through its node and gets ready for one SSH connection.</summary>
    public static async Task<LocaltonetLink> OpenAsync(DeviceSession session, CancellationToken cancellation)
    {
        var (remote, firstBytes) = await MeetDeviceAsync(session, cancellation).ConfigureAwait(false);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var link = new LocaltonetLink(listener, session);
        _ = link.RelayAsync(remote, firstBytes);
        return link;
    }

    /// <summary>
    /// Localtonet learns about the session a moment after it answers, so an early connection can be turned
    /// away. Waiting for the device's first bytes also proves the two ends were joined.
    /// </summary>
    private static async Task<(Socket Socket, byte[] FirstBytes)> MeetDeviceAsync(DeviceSession session, CancellationToken cancellation)
    {
        byte[] preface = Encoding.ASCII.GetBytes($"{Prefix}{ClientRole}{session.SessionId}");
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        window.CancelAfter(ArrivalWindow);

        while (true)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(session.Node, session.Port, window.Token).ConfigureAwait(false);
                await socket.SendAsync(preface, SocketFlags.None, window.Token).ConfigureAwait(false);

                byte[] buffer = new byte[CopyBufferSize];
                int read = await socket.ReceiveAsync(buffer, SocketFlags.None, window.Token).ConfigureAwait(false);
                if (read > 0) return (socket, buffer[..read]);
                socket.Dispose();
            }
            catch (SocketException) when (!window.IsCancellationRequested)
            {
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                if (window.IsCancellationRequested && !cancellation.IsCancellationRequested)
                    throw new TimeoutException(L.T("The device did not respond. Make sure Localtonet is running on it."));
                throw;
            }

            await Task.Delay(RetryDelay, window.Token).ConfigureAwait(false);
        }
    }

    private async Task RelayAsync(Socket remote, byte[] firstBytes)
    {
        Socket? local = null;
        try
        {
            using var accept = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
            accept.CancelAfter(LocalAcceptWindow);
            local = await _listener.AcceptSocketAsync(accept.Token).ConfigureAwait(false);
            local.NoDelay = true;
            _listener.Stop();

            await local.SendAsync(firstBytes, SocketFlags.None, _closed.Token).ConfigureAwait(false);
            await Task.WhenAny(CopyAsync(remote, local), CopyAsync(local, remote)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Closed by either side, or nobody came for the port.
        }
        finally
        {
            _listener.Stop();
            local?.Dispose();
            remote.Dispose();
        }
    }

    private async Task CopyAsync(Socket from, Socket to)
    {
        byte[] buffer = new byte[CopyBufferSize];
        try
        {
            int read;
            while ((read = await from.ReceiveAsync(buffer, SocketFlags.None, _closed.Token).ConfigureAwait(false)) > 0)
                await to.SendAsync(buffer.AsMemory(0, read), SocketFlags.None, _closed.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // The other direction ends the relay.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _closed.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
    }
}

/// <summary>Asking Localtonet for a session, including waiting for the owner to approve it in the browser.</summary>
public static class LocaltonetConnect
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ApprovalWindow = TimeSpan.FromMinutes(5);

    /// <summary>Gets a session for the device. Without a valid access key, <paramref name="needsApproval"/> is told where to approve it.</summary>
    /// <exception cref="InvalidAccessKeyException">The access key does not open this device.</exception>
    public static async Task<DeviceSession> RequestAsync(LocaltonetApi api, string deviceName, string? accessKey, Action<Uri> needsApproval, CancellationToken cancellation)
    {
        ConnectResponse response = await api.ConnectAsync(deviceName, accessKey, cancellation).ConfigureAwait(false);
        if (response.Status == ConnectResponse.Invalid) throw new InvalidAccessKeyException(response.Error ?? L.T("The access key does not open this device."));

        if (response.Status == ConnectResponse.Pending)
        {
            if (api.ApprovalPage(response.ApproveUrl) is { } url) needsApproval(url);

            using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            window.CancelAfter(ApprovalWindow);
            ConnectResponse pending = response;
            try
            {
                while (response.Status == ConnectResponse.Pending)
                {
                    await Task.Delay(PollInterval, window.Token).ConfigureAwait(false);
                    response = await LocaltonetApi.PatientlyAsync(c => api.PollConnectAsync(pending, c), DateTime.MaxValue, window.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                throw new TimeoutException(L.T("The connection was not approved within five minutes."));
            }
        }

        return response.Status == ConnectResponse.Approved && response.Session is { } session
            ? session
            : throw new UnauthorizedAccessException(response.Error ?? L.T("The connection was not approved."));
    }
}

public sealed class InvalidAccessKeyException(string message) : Exception(message);
