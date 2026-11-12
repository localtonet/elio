#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;

using Renci.SshNet.Messages.Connection;

namespace Renci.SshNet.Channels
{
    /// <summary>
    /// A channel the server opened, of a kind the library does not handle itself, kept as a queue of what arrives.
    /// </summary>
    internal sealed class ChannelRemoteStream : ServerChannel
    {
        private readonly BlockingCollection<ArraySegment<byte>> _incoming = new BlockingCollection<ArraySegment<byte>>();

        internal ChannelRemoteStream(ISession session,
                                     uint localChannelNumber,
                                     uint localWindowSize,
                                     uint localPacketSize,
                                     uint remoteChannelNumber,
                                     uint remoteWindowSize,
                                     uint remotePacketSize)
            : base(session, localChannelNumber, localWindowSize, localPacketSize, remoteChannelNumber, remoteWindowSize, remotePacketSize)
        {
        }

        public override ChannelTypes ChannelType
        {
            get { return ChannelTypes.X11; }
        }

        /// <summary>Tells the server the channel is open.</summary>
        public void Accept()
        {
            SendMessage(new ChannelOpenConfirmationMessage(RemoteChannelNumber, LocalWindowSize, LocalPacketSize, LocalChannelNumber));
        }

        /// <summary>Waits for the next piece the server sent.</summary>
        /// <returns><see langword="false"/> once the server has finished sending or the channel is gone.</returns>
        public bool TryTake(out ArraySegment<byte> data)
        {
            try
            {
                return _incoming.TryTake(out data, System.Threading.Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                data = default;
                return false;
            }
        }

        protected override void OnData(ArraySegment<byte> data)
        {
            base.OnData(data);

            // The session reuses its buffer for the next message.
            var copy = new byte[data.Count];
            Buffer.BlockCopy(data.Array!, data.Offset, copy, 0, data.Count);
            try
            {
                _incoming.Add(new ArraySegment<byte>(copy));
            }
            catch (InvalidOperationException)
            {
                // Nobody is reading any more.
            }
        }

        protected override void OnEof()
        {
            base.OnEof();
            Finish();
        }

        protected override void OnClose()
        {
            Finish();
            base.OnClose();
        }

        protected override void OnDisconnected()
        {
            base.OnDisconnected();
            Finish();
        }

        protected override void OnErrorOccurred(Exception exp)
        {
            base.OnErrorOccurred(exp);
            Finish();
        }

        private void Finish()
        {
            try
            {
                _incoming.CompleteAdding();
            }
            catch (ObjectDisposedException)
            {
                // Already closed from this side.
            }
        }

        protected override void Dispose(bool disposing)
        {
            Finish();
            base.Dispose(disposing);
        }
    }

    /// <summary>What the application is given for such a channel: reading takes what the server sent, writing sends to it.</summary>
    internal sealed class RemoteChannelStream : Stream
    {
        private readonly ChannelRemoteStream _channel;
        private ArraySegment<byte> _current;
        private bool _disposed;

        public RemoteChannelStream(ChannelRemoteStream channel)
        {
            _channel = channel;
        }

        public override bool CanRead
        {
            get { return true; }
        }

        public override bool CanSeek
        {
            get { return false; }
        }

        public override bool CanWrite
        {
            get { return true; }
        }

        public override long Length
        {
            get { throw new NotSupportedException(); }
        }

        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0)
            {
                return 0;
            }

            while (_current.Count == 0)
            {
                if (!_channel.TryTake(out _current))
                {
                    return 0;
                }
            }

            var taken = Math.Min(count, _current.Count);
            Buffer.BlockCopy(_current.Array!, _current.Offset, buffer, offset, taken);
            _current = new ArraySegment<byte>(_current.Array!, _current.Offset + taken, _current.Count - taken);
            return taken;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_channel.IsOpen)
            {
                throw new IOException("The channel is closed.");
            }

            _channel.SendData(buffer, offset, count);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _channel.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
