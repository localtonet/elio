## What this changes

<!-- What is different for the person using Elio, in a sentence or two. For a change to the interface, a screenshot helps. -->

Fixes #

## How it was checked

<!-- The systems you tried it on, and what you did. "Connected to Ubuntu 24.04 over a jump host and copied a 2 GB file" says more than "tested". -->

## Checklist

- [ ] `dotnet build src/Elio.Desktop -c Release` passes with no new warnings.
- [ ] `dotnet test tests/Elio.Tests` passes, and the change has a test where it can have one.
- [ ] The README (or `docs/PLUGINS.md`) says what Elio now does, if that changed.
- [ ] The change is noted in `CHANGELOG.md` under "Unreleased".
- [ ] New text in the interface goes through `L.T("...")`.
- [ ] This pull request holds one change, and nothing it does not need.

<!-- See CONTRIBUTING.md for more. A security problem goes to SECURITY.md, not to a pull request. -->
