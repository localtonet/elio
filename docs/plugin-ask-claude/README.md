# Ask Claude

An Elio plugin that asks Claude what some terminal output means: an error, a stack trace, a log, a command that did not do what you expected.

It is a plugin rather than part of Elio on purpose. Elio sends nothing to anyone unless you set it up to, and this way the feature, its API key and its connection to Anthropic exist only for those who install it.

## Setting it up

1. Install Python 3 and the Anthropic SDK: `pip install anthropic`.
2. Give the SDK your API key, from [platform.claude.com](https://platform.claude.com): set `ANTHROPIC_API_KEY` in your environment, or run `ant auth login`. Start Elio again afterwards so it sees the change.
3. Copy this folder into `plugins` in Elio's data folder (`%LOCALAPPDATA%\Elio\plugins\ask-claude` on Windows), and allow it when Elio asks.

## Using it

Select some text in a terminal, or just run a command in a shell that marks its commands (**Mark commands in this shell** in the palette), then choose **Ask Claude about this output** in the palette. Elio says how many characters would go to Anthropic and asks for an optional question; nothing is sent if you back out. The answer opens in a window you can copy from.

What is sent: the selected text, or the last command's output (at most its last 100 000 characters), your question, and the folder the terminal is in. It goes to Anthropic under your own API key and account. The plugin uses `claude-opus-5-5` at low effort, and, if that model declines a question for safety reasons, lets the API hand it to another model (the `fallbacks` option).
