# Security

Elio handles credentials that grant access to your servers. We take that responsibility seriously and welcome reports that help us keep Elio secure.

## Reporting a vulnerability

Please report security vulnerabilities privately rather than opening a public issue.

Use **Report a vulnerability** in the repository’s **Security** tab to start a private conversation with the maintainers.

To help us investigate, include as much of the following as you can:

- The Elio version and operating system.
- What an attacker needs to control to exploit the issue, such as a remote server, an imported file, another user account on the same device, the sync server, or network traffic.
- Steps to reproduce the issue, along with a minimal example or proof of concept if available.

You do not need a complete analysis to submit a report. If you suspect a security issue, please share what you have found.

You can expect an initial response within one week. Once a fix is released, we will publish the report and credit your contribution, unless you prefer to remain anonymous.

## Scope

This policy covers:

- The Elio desktop and mobile applications.
- The sync server in `server/Elio.SyncServer`.
- The bundled copy of SSH.NET in `vendor/SSH.NET`.

We are particularly interested in vulnerabilities that allow a remote server, terminal output, an opened or imported file, or the sync server to expose credentials or other secrets, execute unauthorized code on your device, or gain unauthorized access to other servers.

## Security boundaries

Elio is not designed to protect against malicious software already running with your user account’s permissions on your device. That level of local compromise is outside Elio’s security boundary.

For details on the protections Elio provides and their limitations, see the [Security section of the README](README.md#security).

## Supported versions

Security fixes are provided in the latest release of Elio.
