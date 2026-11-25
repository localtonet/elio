# Contributing to Elio

Thank you for helping. Bug reports, fixes, translations and new features are all welcome. This page says how to build Elio, how to check a change, and what a pull request needs.

Found a security problem? Do not open an issue for it. [SECURITY.md](SECURITY.md) says how to report it privately.

## Before you start

- **A bug:** open an issue with the version, the system and the steps that show it. If you want to fix it yourself, say so in the issue.
- **A new feature or a larger change:** open an issue first and describe what you want to do. A short talk before the work saves a pull request that goes in a direction the project cannot take.
- **A small fix** (a typo, a wrong translation, a clear bug with an obvious fix): send the pull request straight away.

Everyone taking part is expected to follow the [code of conduct](CODE_OF_CONDUCT.md).

## Building

Elio targets .NET 10. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download), then from the repository root:

```bash
dotnet run --project src/Elio.Desktop
```

The terminal engine, libghostty-vt, is in `native/out` for Windows (x64) and Linux (x64), so on those systems that is all you need. On a Mac, or for the other systems, build the engine first; [Building from source](README.md#building-from-source) in the README has the commands, and also covers the phone apps, single-file builds and packaging.

The phone apps need workloads that the desktop app does not:

```bash
dotnet workload install android ios
```

## Testing

```bash
dotnet test tests/Elio.Tests
```

The tests need no workloads, no network and no terminal engine, and they never touch your own Elio data: each run points Elio's data folder at a temporary one. They run on Windows, Linux and macOS in CI, and every pull request has to pass them on all three.

Add a test with your change wherever the change can be tested without a window or a network: parsing, file handling, the vault, sync, importers, the updater and so on. `tests/Elio.Tests` has many examples. A bug fix is best sent with the test that failed before it.

Warnings are errors in this repository (`TreatWarningsAsErrors`), so a build with a new warning fails. Fix the warning rather than turning it off. CI also runs a trimmed publish on Linux, which turns code that trimming would break into errors:

```bash
dotnet publish src/Elio.Desktop -c Release -r linux-x64 -o publish
```

## Adding or correcting a translation

Translations are plain JSON files in `src/Elio.Core/Languages`, one per language. Each maps the English text, exactly as it is written in the code, to the translated text:

```json
{
  "A name is required.": "Ein Name ist erforderlich."
}
```

- **To correct a translation,** change the text on the right and leave the English on the left as it is. Corrections from native speakers are especially welcome: many of the translations were machine-made and have not been reviewed yet.
- **To add a language,** copy an existing file to `<code>.json`, where the code is a language tag such as `pt` or `zh-Hans`, and translate the right-hand side. Then add the language's name, written in that language, to `_names.json`. A language written from right to left also goes in `RightToLeftCodes` in `src/Elio.Core/Localization.cs`.
- Keep placeholders such as `{0}` and `{1}` as they are; they can move within the sentence. Keep the names of buttons and settings the same as they are in the rest of your file, so that a sentence that names a button names it the way the button reads.
- A text that is missing, or left empty, is shown in English.

`dotnet test tests/Elio.Tests` checks every file: that it is a flat map of text to text, that no English text is listed twice, that every translation has the placeholders of its English text, and that `_names.json` lists exactly the languages that have a file.

The README and the website list the languages and say how many there are; update both when you add one.

## Plugins

A plugin is a separate program that talks to Elio over its standard input and output, so writing one needs no change to Elio itself. The protocol is described in [docs/PLUGINS.md](docs/PLUGINS.md), and a working example is in [docs/plugin-example](docs/plugin-example). A change to the protocol is a change to Elio; open an issue for it first.

## Pull requests

- **One change per pull request.** A fix and an unrelated clean-up are two pull requests.
- **Say what changes for the person using Elio**, and how you checked it. For a change to the interface, a screenshot helps.
- **Tests and a build that pass:** `dotnet build src/Elio.Desktop -c Release` and `dotnet test tests/Elio.Tests`.
- **Keep the documentation true.** When you change what Elio does, change the README (and `docs/PLUGINS.md` for the plugin protocol) in the same pull request. New text in the interface goes through `L.T("...")` so that it can be translated; you do not have to translate it yourself.
- **Note the change in [CHANGELOG.md](CHANGELOG.md)**, under "Unreleased", in a line written for the person using Elio.
- **Plain commit messages:** a first line that says what the commit does, in the imperative ("Add an AppImage for Linux"), and below it, if it helps, why.
- **Leave out what you did not mean to change**, such as reformatted files, editor settings, build output or a new dependency that the change does not need. A new package is a decision in its own right; say why it is needed.
- **Security-sensitive code** (the vault, sync, the updater, host keys, anything that handles passwords or keys) gets a closer review. Explain what an attacker could and could not do after your change.

By sending a pull request you agree that your contribution is licensed under the [Apache License 2.0](LICENSE), like the rest of Elio.

## How Elio is written about

The README, the interface and the website share one style. Please keep it in what you write:

- **Plain and concrete.** Say what happens, in the words the person using Elio would use: "Elio asks before it runs a plugin for the first time", not "plugin execution is gated behind user consent".
- **Name things the way the interface names them,** in bold: **Split right**, **Check for updates**. Keys and commands go in code: `Ctrl+Shift+P`, `dotnet test`.
- **Short sentences, active voice, no hype.** No "seamless", "powerful" or "blazing fast"; say what it does and let the reader judge. If a limitation matters, say it plainly.
- **Comments in the code follow the same style.** They say why, not what, in full sentences.

## Repository layout

[Repository layout](README.md#repository-layout) in the README says which project holds what. The scripts in `build/` build the terminal engine and package the apps; `.github/workflows` has the CI and release workflows.
