#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

using Renci.SshNet.Abstractions;
using Renci.SshNet.Channels;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Connection;

namespace Renci.SshNet
{
    /// <summary>What to ask of the server before a shell is started, and what it said.</summary>
    public sealed class ShellRequests
    {
        /// <summary>The channel type the server uses to reach a forwarded SSH agent.</summary>
        public const string AgentChannel = "auth-agent@openssh.com";

        /// <summary>The channel type the server uses for each forwarded X11 window connection.</summary>
        public const string X11Channel = "x11";

        /// <summary>Gets or sets a value indicating whether to ask for the SSH agent to be forwarded.</summary>
        public bool AgentForwarding { get; set; }

        /// <summary>Gets a value indicating whether the server agreed to forward the agent.</summary>
        public bool AgentForwardingAccepted { get; internal set; }

        /// <summary>
        /// Gets or sets the cookie programs on the server must present, as hexadecimal text. Set it to ask for X11 forwarding.
        /// </summary>
        public string? X11Cookie { get; set; }

        /// <summary>Gets or sets the X11 screen number.</summary>
        public uint X11Screen { get; set; }

        /// <summary>Gets a value indicating whether the server agreed to forward X11.</summary>
        public bool X11Accepted { get; internal set; }

        /// <summary>Gets the environment variables to ask the server to set. Servers only take the names they allow.</summary>
        public IDictionary<string, string> Environment { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <content>Channels the server opens towards the client, such as a forwarded agent or X11.</content>
    public partial class SshClient
    {
        private readonly ConcurrentDictionary<string, Action<Stream>> _channelHandlers = new ConcurrentDictionary<string, Action<Stream>>(StringComparer.Ordinal);
        private readonly object _channelHandlerGate = new object();
        private ISession? _channelHandlerSession;

        /// <summary>
        /// Accepts channels of one type that the server opens. Each gets its own thread, and the channel is closed when the
        /// handler returns. Channels of a type nobody accepts are refused.
        /// </summary>
        /// <param name="channelType">The channel type, for example <see cref="ShellRequests.AgentChannel"/>.</param>
        /// <param name="handler">Given a stream that reads what the server sends and writes back to it.</param>
        public void AcceptChannels(string channelType, Action<Stream> handler)
        {
            ArgumentNullException.ThrowIfNull(channelType);
            ArgumentNullException.ThrowIfNull(handler);

            EnsureSessionIsOpen();

            _channelHandlers[channelType] = handler;

            lock (_channelHandlerGate)
            {
                if (!ReferenceEquals(_channelHandlerSession, Session))
                {
                    _channelHandlerSession = Session;
                    Session!.ChannelOpenReceived += Session_RemoteChannelOpening;

                    // Until someone asks, the session treats a channel opened by the server as a protocol error.
                    Session.RegisterMessage("SSH_MSG_CHANNEL_OPEN");
                }
            }
        }

        /// <summary>
        /// Starts a shell with a terminal, first making the extra requests in <paramref name="requests"/>.
        /// </summary>
        /// <returns>The shell.</returns>
        public ShellStream CreateShellStream(string terminalName, uint columns, uint rows, uint width, uint height, int bufferSize, ShellRequests requests)
        {
            ArgumentNullException.ThrowIfNull(requests);

            EnsureSessionIsOpen();

            return new ShellStream(Session!, terminalName, columns, rows, width, height, terminalModeValues: null, bufferSize, requests);
        }

        private void Session_RemoteChannelOpening(object? sender, MessageEventArgs<ChannelOpenMessage> e)
        {
            var message = e.Message;

            // Remote port forwards answer for their own.
            if (message.Info is ForwardedTcpipChannelInfo || sender is not Session session)
            {
                return;
            }

            if (!_channelHandlers.TryGetValue(message.Info.ChannelType, out var handler))
            {
                session.SendMessage(new ChannelOpenFailureMessage(message.LocalChannelNumber, string.Empty, ChannelOpenFailureMessage.AdministrativelyProhibited));
                return;
            }

            ThreadAbstraction.ExecuteThread(() =>
            {
                ChannelRemoteStream channel;
                try
                {
                    channel = session.CreateChannelRemoteStream(message.LocalChannelNumber, message.InitialWindowSize, message.MaximumPacketSize);
                    channel.Accept();
                }
                catch (Exception)
                {
                    return;
                }

                using (var stream = new RemoteChannelStream(channel))
                {
                    try
                    {
                        handler(stream);
                    }
                    catch (Exception)
                    {
                        // The handler's failure ends its own channel and nothing else.
                    }
                }
            });
        }
    }
}
