#nullable enable
using Renci.SshNet.Security.Cryptography;

namespace Renci.SshNet
{
    /// <summary>
    /// The key derivation OpenSSH protects a private key file with: bcrypt_pbkdf. SSH.NET has it for reading such a
    /// file and keeps it to itself; this hands it to the application, which needs the same one to write a file that
    /// can be read back.
    /// </summary>
    public static class OpenSshKdf
    {
        /// <summary>Fills <paramref name="output"/> with the key and IV that a passphrase, a salt and a number of rounds lead to.</summary>
        public static void Derive(byte[] passphrase, byte[] salt, int rounds, byte[] output) =>
            new BCrypt().Pbkdf(passphrase, salt, rounds, output);
    }
}
