# Changelog

What changed in each release of Elio. The newest release is at the top, and what is done but not released yet is under "Unreleased".

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Each release is a tag such as `v0.1.8`, and its files are on the [releases page](https://github.com/localtonet/elio/releases).

## [Unreleased]

### Added

- A build for Windows on ARM (`elio-win-arm64.zip`) beside the x64 one. It updates itself from its own zip, never from the x64 one, and Scoop offers it on ARM computers.
- An AppImage for Linux, for x64 and ARM64, beside the tar.gz and the .deb. Elio started from an AppImage says when a new release is out and opens the download page, as a .deb install does, instead of replacing itself.
- The release workflow can build the iPhone and iPad app and send it to TestFlight, once the App Store Connect secrets are set up.
- Every change is built and tested on Windows and macOS as well as Linux.
- CONTRIBUTING.md, a code of conduct, issue and pull request templates, and this changelog.
- A command line: `elio ssh [-p port] [-l user] [-i key] [user@]host[:port]`, `elio connect <server>`, `elio workspace <name>`, `elio --help` and `elio --version`. A running Elio takes them in its own window.
- `ssh://`, `elio://connect/<server>` and `elio://workspace/<name>` links open in Elio, after asking. Windows and Linux register them from Settings, the Mac app and the phone apps on their own.
- Snippets can have blanks to fill in, written `{{name}}` or `{{name:default}}`, and can be put in folders.
- Workspaces are synced.
- Connecting through a command (ProxyCommand), shown and agreed to before it first runs on each device, and environment variables sent to SSH servers (SetEnv). Both are read from `~/.ssh/config` on import.
- **Export servers as an OpenSSH config file**.
- Links that programs mark with OSC 8 open on Ctrl+click, and Ctrl over a link shows where it goes.
- **Record this terminal (asciinema) / stop** records a terminal as an asciicast.
- **Tell me when the output shows** in Settings: words or regular expressions that mark a tab and ask for attention when a line of output matches.
- Imports from Hetzner Cloud, Linode, Vultr and Tailscale.
- **Kubernetes pods** in the palette opens a shell in a running pod through kubectl.
- `read_file` and `list_directory` for AI tools connected with `elio --mcp`, read-only and under the same permissions as `run_command`.
- Screen readers get a name for every icon button and form field.
- When Elio fails, a report is written to its logs folder and mentioned at the next start. Nothing is sent.
- One SSH connection can be shared by every terminal to a server (like ControlMaster), per server.
- A keep-alive interval per server, and saving or recording every session with a server.
- Snippets can be for some servers or groups only.
- Passwords can come from 1Password (`op://…`) or Bitwarden (`bw://…`) through their command line tools.
- A rule in **Tell me when the output shows** written `pattern => text` types the text and Enter when the pattern shows.
- Plugins can list the servers, read the last command's output, open a server, offer a choice, show a longer text and say they are still at work (`servers/list`, `terminal/lastOutput`, `terminal/connect`, `choose`, `show`, `ping`).
- An example plugin, `docs/plugin-ask-claude`, that asks Claude what the output in a terminal means, with your own API key.
- Sixel pictures are shown, translated into the pictures the terminal already draws; the terminal says it can show them, and answers questions about its size in pixels.
- Key releases and repeats reach programs that use the Kitty keyboard protocol.
- Imports from Azure and Google Cloud through `az` and `gcloud`, and from Proxmox VE with an API token and a pinned certificate.
- **Docker containers…** on a saved server opens a shell in a container running there.
- Permissions on several files and on a folder's contents; a speed limit for transfers; comparing and synchronising the two sides of the file browser.

### Changed

- The README and the website point to the Android app on Google Play, with the APK as a second choice.

### Fixed

- One message whose tool name was not text could end `elio --mcp`.
- One unreadable `plugin.json` (a NUL in its command, or text that is not UTF-8) kept every plugin from loading. It is now listed as broken on its own.
- A vault key file behind Windows Hello written by a newer Elio, or damaged, kept the app from starting. The vault now starts locked and says why.
- A shared terminal stopped without saying so when the relay sent a message larger than allowed.
- tmux control mode kept a line without an end in memory however long it grew. Lines and replies now have a limit.
- Two devices that changed the same saved password at the same moment kept different ones and synced again and again.
- Pictures placed by cells (Kitty graphics) were not drawn until the window was resized.
- Zmodem: "OO" was printed after every `sz` download; a new file before the last one ended left the last one open; five CAN bytes inside a data block were not taken as a cancel; a start after a stray `*` was missed.

- The text added with the editor, preview, archives, content search, the updater and the access key prompts was shown in English in every other language. It is now translated into all of them.
- The website on phones: the download cards no longer run off the screen, the header links fit without scrolling sideways, commands in code blocks wrap, and buttons are large enough to tap.

## [0.1.8] - 2026-11-20

### Added

- A built-in editor for text files in the file panels (`F4`, or **Edit**), on this computer, on a server and on a phone, with find and replace, go to line and word wrap.
- **Preview** (`Space`) shows text, pictures, the names in a folder, or the first bytes of any other file, without copying it anywhere.
- **New file**, **Copy**, **Cut**, **Paste** and **Duplicate** in the file panels, and **Rename several** (`F2`) with find and replace, numbering and changes of case.
- Archives: **Extract here**, **Extract to a new folder** and **Compress...**, on a server and on this computer.
- **Search inside files** below a folder, with `grep` or `rg` on a server.
- Folders you go to often, under the pin button.
- The command line is colored as it is typed: commands, options, strings, variables and pipes.

### Changed

- On a phone, the file screen puts the two lists one above the other.
- The README no longer offers `winget install`, because the package cannot be submitted to winget for now.

## [0.1.7] - 2026-11-18

### Fixed

- The Android app no longer fails to start when the terminal engine is missing; each terminal says so instead. A release build for Android now stops if the engine was not built.
- Google Play no longer leaves out tablets and other devices that cannot be held upright.

## [0.1.6] - 2026-11-17

### Added

- After you approve a connection to a Localtonet device that has no access key yet, Elio offers to get the keys.
- Elio shows its version in the sidebar and at the top of Settings.

### Changed

- Local shells open from the terminal button above the server list, and **This computer** is only listed in the sidebar while there is no server or device yet.
- The version check runs every six hours instead of once a day.

## [0.1.5] - 2026-11-16

### Added

- AI tools connected with `elio --mcp` see your Localtonet devices too, and can run commands on the ones you allow.
- **Edit** in a Localtonet device's right-click menu gives it a name of your own and decides whether AI tools may run commands on it.
- Manifests for Homebrew, Scoop and winget are written for each release.

## [0.1.4] - 2026-11-13

### Added

- Search in the file panels (`Ctrl+F`): by name, with `*` and `?` patterns or a regular expression, and optionally in every folder below.
- Typing in a file list moves to the name that starts with the letters, `Ctrl+H` hides hidden files on both sides, the sorted column shows an arrow, and `Ctrl+L` edits the address.

## [0.1.3] - 2026-11-13

### Added

- In-app updates: **Update and restart** downloads a release, checks its signed manifest (`update.json`) and the file against it, and swaps the new version in.
- Ten languages: Czech, Greek, Hebrew, Marathi, Romanian, Swahili, Swedish, Tamil, Telugu and Urdu, for 30 in all. Hebrew and Urdu are laid out right to left.
- A privacy policy on the website and in the README, and a code signing policy.

### Changed

- All translations were reviewed: developer terms stay recognisable, wrong meanings were fixed, and each language uses one register and one quote style.
- SECURITY.md says more clearly how to report a problem and what is covered.

## [0.1.2] - 2026-11-12

### Fixed

- The sync server and the website no longer both listen on port 5000, so they can run side by side: the website keeps 5000 and the sync server takes 5001.

## [0.1.1] - 2026-11-12

### Changed

- Release files are named without the version (such as `elio-win-x64.zip`), so that a link to the latest release keeps working.
- The website links to the repository and to each platform's download.

### Fixed

- Building the macOS terminal engine on GitHub's Mac runners.

## [0.1.0] - 2026-11-12

The first release: a terminal and SSH client for Windows, macOS, Linux, iOS and Android, with the sync server and the website.

### Added

- A terminal drawn with libghostty-vt, Skia and HarfBuzz, with tabs, split panes, more than 200 color themes, find in output, shell marks, command suggestions, command history, and pictures through the Kitty graphics protocol.
- SSH with keys, certificates, the SSH agent, FIDO2 security keys, jump hosts, proxies, port forwarding, agent and X11 forwarding; Mosh; native `tmux -CC`; Telnet; serial ports; local shells and Docker containers.
- A server library with groups, group defaults, tags, a keychain of identities, key generation, and imports from OpenSSH, PuTTY, Ansible, CSV, DigitalOcean and AWS.
- Many servers at once: open a group side by side, workspaces, broadcast input, snippets, and running one command on several servers.
- A two-pane SFTP file browser, server to server copies, a transfer queue with resume, edit in place, and Zmodem.
- Sharing a terminal with a link and a password, encrypted from end to end.
- End-to-end encrypted sync between devices, with an open-source sync server you can run yourself.
- Localtonet devices in the sidebar, reached without opening a port.
- An MCP server (`elio --mcp`) for AI tools, plugins, 20 languages, keyboard shortcuts that can be changed, and a layout for phones with an app lock.

[Unreleased]: https://github.com/localtonet/elio/compare/v0.1.8...HEAD
[0.1.8]: https://github.com/localtonet/elio/compare/v0.1.7...v0.1.8
[0.1.7]: https://github.com/localtonet/elio/compare/v0.1.6...v0.1.7
[0.1.6]: https://github.com/localtonet/elio/compare/v0.1.5...v0.1.6
[0.1.5]: https://github.com/localtonet/elio/compare/v0.1.4...v0.1.5
[0.1.4]: https://github.com/localtonet/elio/compare/v0.1.3...v0.1.4
[0.1.3]: https://github.com/localtonet/elio/compare/v0.1.2...v0.1.3
[0.1.2]: https://github.com/localtonet/elio/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/localtonet/elio/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/localtonet/elio/releases/tag/v0.1.0
