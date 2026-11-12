# SSH.NET, Elio's copy

This folder holds the source of [SSH.NET](https://github.com/sshnet/SSH.NET) at release `2026.0.0`
(commit `7b2fd3dbf2c86a80a7b06cea020aa5f821c9902e`), with a small number of changes Elio needs. SSH.NET is
licensed under the MIT license; see [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.TXT](THIRD-PARTY-NOTICES.TXT).

Only the library itself is here. Its tests, documentation and build tooling stay upstream.

## What was changed

Everything new lives in the `Elio` folder. Changes to upstream files are marked with a comment that starts with `Elio:`.

| Change | Where | Why |
| --- | --- | --- |
| Builds for `net10.0` only, without the upstream analyzers, signing and version tooling | `Renci.SshNet.csproj`, `Directory.Build.props`, `Properties/ThisAssembly.cs`, `Properties/AssemblyInfo.cs` | One target is all Elio ships |
| AES-GCM is offered before AES-CTR | `ConnectionInfo.cs` | It uses the processor's AES instructions and needs no separate MAC pass, which makes file transfers faster |
| `SftpFileStream.WriteAtAsync` and `MaxWriteLength` | `Sftp/SftpFileStream.cs` | Sends a write without waiting for the one before it, so a file can be uploaded as a stream of requests on one handle, the way OpenSSH's `sftp` does |
| Channels opened by the server are handed to the application as streams: `SshClient.AcceptChannels` | `Elio/SshClient.Forwarding.cs`, `Elio/ChannelRemoteStream.cs`, `Session.cs`, `Messages/Connection/ChannelOpen/ChannelOpenMessage.cs` | Agent forwarding and X11 forwarding. An unknown channel type is now refused instead of ending the session |
| `ShellRequests` and a `CreateShellStream` overload that takes it | `Elio/SshClient.Forwarding.cs`, `ShellStream.cs`, `Channels/ChannelSession.cs`, `Channels/IChannelSession.cs`, `Elio/ForwardingMessages.cs` | Asks for agent forwarding, X11 forwarding and environment variables before the shell starts |
| OpenSSH key files for FIDO2 security keys are recognised: `SecurityKeyFile`, `SecurityKeyFileException` | `Elio/SecurityKeyFile.cs`, `PrivateKeyFile.OpenSSH.cs` | Such a file holds no private key, so its fields are passed to the application, which signs with the device |
| The key derivation of OpenSSH key files is public: `OpenSshKdf.Derive` | `Elio/OpenSshKdf.cs` | Elio generates keys and writes them with a passphrase in the format SSH.NET reads, which needs the same bcrypt_pbkdf. Nothing upstream is touched |

## Updating to a newer SSH.NET

Copy the new `src/Renci.SshNet` over this folder, keep the files listed in the first row and the `Elio` folder,
and reapply the marked changes: searching for `Elio:` finds each of them.
