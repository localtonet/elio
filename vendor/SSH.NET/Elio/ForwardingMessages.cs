#nullable enable
namespace Renci.SshNet.Messages.Connection
{
    /// <summary>Asks the server to let programs in this session reach the user's SSH agent.</summary>
    internal sealed class AgentForwardingRequestInfo : RequestInfo
    {
        public const string Name = "auth-agent-req@openssh.com";

        public AgentForwardingRequestInfo()
        {
            WantReply = true;
        }

        public override string RequestName
        {
            get { return Name; }
        }
    }

    /// <summary>A request from the server to open a kind of channel the library has no class of its own for.</summary>
    internal sealed class OtherChannelOpenInfo : ChannelOpenInfo
    {
        private readonly string _channelType;

        public OtherChannelOpenInfo(string channelType)
        {
            _channelType = channelType;
        }

        public override string ChannelType
        {
            get { return _channelType; }
        }
    }
}
