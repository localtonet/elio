# Security

Elio holds the keys to other people's servers, so reports of security problems are taken seriously and are
welcome.

## Reporting a problem

Please do not open a public issue. Use **Report a vulnerability** on the repository's **Security** tab, which
opens a private conversation with the maintainers.

Include what you can of:

- the version of Elio and the operating system,
- what an attacker has to control (a server you connect to, a file you import, another account on the same
  computer, the sync server, the network),
- the steps that show the problem.

You can expect an answer within a week. Once a fix is released, the report is published with credit to you,
unless you would rather stay unnamed.

## What counts

- The desktop and mobile apps, the sync server in `server/Elio.SyncServer` and the copy of SSH.NET in
  `vendor/SSH.NET`.
- Anything that lets a server you connect to, a file you open or import, a terminal you watch, or the sync
  server read your secrets, run something on your computer, or reach your other servers.

A program already running as you on your own computer can read what you can read; that is outside what Elio
can defend against. The [Security](README.md#security) section of the README describes what is protected
and how.

## Supported versions

Fixes go into the latest release.
