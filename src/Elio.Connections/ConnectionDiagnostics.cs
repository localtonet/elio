using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Elio.Core;

namespace Elio.Connections;

public sealed record DiagnosticStep(string Name, bool Passed, string Detail);

/// <summary>Works out how far a connection gets, so a failure can name the step that broke.</summary>
public static class ConnectionDiagnostics
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);
    private const int MaxBannerShown = 120;

    /// <summary>Tries name lookup, then TCP, then reading the SSH banner. Stops at the first step that fails.</summary>
    /// <param name="expectSsh">False for a server that speaks something else, where the check ends once the port answers.</param>
    public static async Task<List<DiagnosticStep>> RunAsync(string host, int port, CancellationToken cancellation, bool expectSsh = true)
    {
        var steps = new List<DiagnosticStep>();
        var clock = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(StepTimeout);

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            steps.Add(new DiagnosticStep(L.T("Name lookup"), false, ex.Message));
            return steps;
        }

        steps.Add(new DiagnosticStep(L.T("Name lookup"), true, $"{string.Join(", ", addresses.Take(3))} ({clock.ElapsedMilliseconds} ms)"));

        clock.Restart();
        timeout.CancelAfter(StepTimeout);
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(addresses, port, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            steps.Add(new DiagnosticStep(L.T("TCP connection"), false, L.T("Port {0}: no reply in {1:0} seconds. A firewall may be blocking it.", port, StepTimeout.TotalSeconds)));
            return steps;
        }
        catch (SocketException ex)
        {
            steps.Add(new DiagnosticStep(L.T("TCP connection"), false, L.T("Port {0}: {1}", port, ex.Message)));
            return steps;
        }

        steps.Add(new DiagnosticStep(L.T("TCP connection"), true, L.T("Port {0} is open ({1} ms)", port, clock.ElapsedMilliseconds)));
        if (!expectSsh) return steps;

        clock.Restart();
        timeout.CancelAfter(StepTimeout);
        byte[] buffer = new byte[256];
        try
        {
            int read = await tcp.GetStream().ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            string banner = Encoding.ASCII.GetString(buffer, 0, read).Trim();
            if (banner.Length > MaxBannerShown) banner = banner[..MaxBannerShown];

            bool isSsh = banner.StartsWith("SSH-", StringComparison.Ordinal);
            steps.Add(new DiagnosticStep(L.T("SSH banner"), isSsh, isSsh ? $"{banner} ({clock.ElapsedMilliseconds} ms)" : L.T("This port does not answer like SSH: {0}", banner)));
        }
        catch (OperationCanceledException)
        {
            steps.Add(new DiagnosticStep(L.T("SSH banner"), false, L.T("The server did not answer in {0:0} seconds. This port may not be SSH.", StepTimeout.TotalSeconds)));
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            steps.Add(new DiagnosticStep(L.T("SSH banner"), false, ex.Message));
        }

        return steps;
    }
}
