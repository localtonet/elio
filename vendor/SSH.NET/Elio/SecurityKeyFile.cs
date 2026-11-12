#nullable enable
using System;
using System.Text;

using Renci.SshNet.Common;

namespace Renci.SshNet
{
    /// <summary>
    /// What an OpenSSH key file for a FIDO2 security key holds. There is no private key in it: that never leaves the
    /// device, and the file only says which credential on the device to ask.
    /// </summary>
    public sealed class SecurityKeyFile
    {
        /// <summary>The key type for an Ed25519 credential.</summary>
        public const string Ed25519 = "sk-ssh-ed25519@openssh.com";

        /// <summary>The key type for an ECDSA P-256 credential.</summary>
        public const string Ecdsa = "sk-ecdsa-sha2-nistp256@openssh.com";

        /// <summary>The flag that says the device must verify the user, with a PIN or a fingerprint, for each signature.</summary>
        public const byte VerifyUserFlag = 0x04;

        internal SecurityKeyFile(string keyType, byte[] publicKey, string application, byte flags, byte[] keyHandle, string comment)
        {
            KeyType = keyType;
            PublicKey = publicKey;
            Application = application;
            Flags = flags;
            KeyHandle = keyHandle;
            Comment = comment;
        }

        /// <summary>Gets the key type: <see cref="Ed25519"/> or <see cref="Ecdsa"/>.</summary>
        public string KeyType { get; }

        /// <summary>Gets the public key the way it is sent to a server.</summary>
        public byte[] PublicKey { get; }

        /// <summary>Gets the application the credential was made for, usually "ssh:".</summary>
        public string Application { get; }

        /// <summary>Gets the flags the key was made with.</summary>
        public byte Flags { get; }

        /// <summary>Gets what the device needs to find the credential.</summary>
        public byte[] KeyHandle { get; }

        /// <summary>Gets the comment stored with the key.</summary>
        public string Comment { get; }

        internal static bool IsSecurityKeyType(string keyType)
        {
            return keyType is Ed25519 or Ecdsa;
        }

        /// <summary>Reads the fields that follow the key type in the private part of the file.</summary>
        internal static SecurityKeyFile Read(string keyType, SshDataStream reader)
        {
            using var publicKey = new SshDataStream(256);
            publicKey.Write(keyType, Encoding.ASCII);

            if (keyType == Ecdsa)
            {
                publicKey.Write(reader.ReadString(Encoding.ASCII), Encoding.ASCII);
            }

            publicKey.WriteBinary(reader.ReadBinary());

            var application = reader.ReadString(Encoding.UTF8);
            publicKey.Write(application, Encoding.UTF8);

            var flags = reader.ReadByte();
            if (flags < 0)
            {
                throw new SshException("The security key file is cut short.");
            }

            var keyHandle = reader.ReadBinary();
            _ = reader.ReadBinary();
            var comment = reader.ReadString(Encoding.UTF8);

            return new SecurityKeyFile(keyType, publicKey.ToArray(), application, (byte)flags, keyHandle, comment);
        }
    }

    /// <summary>
    /// Thrown in place of a key when the file read is for a FIDO2 security key, because a <see cref="PrivateKeyFile"/>
    /// has to hold a private key and this kind has none. The caller signs with the device instead.
    /// </summary>
    public sealed class SecurityKeyFileException : SshException
    {
        internal SecurityKeyFileException(SecurityKeyFile file)
            : base("This key lives on a security key.")
        {
            File = file;
        }

        /// <summary>Gets what the file said.</summary>
        public SecurityKeyFile File { get; }
    }
}
