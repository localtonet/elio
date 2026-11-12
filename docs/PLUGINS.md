# Elio plugins

A plugin adds commands to Elio. It is an ordinary program, in any language, that Elio starts and
talks to over its standard input and output. Because it is a separate process, a plugin that hangs
or crashes takes only itself down.

A complete example is in [`plugin-example`](plugin-example): copy that folder into the plugins
folder and Elio gains a "Show the date" command.

## Where plugins live

Each plugin is a folder with a `plugin.json` in it, inside `plugins` in Elio's data folder:

| | |
|---|---|
| Windows | `%LOCALAPPDATA%\Elio\plugins\<name>\plugin.json` |
| Portable mode | `data\plugins\<name>\plugin.json` beside the program |
| `ELIO_DATA_DIR` set | `$ELIO_DATA_DIR/plugins/<name>/plugin.json` |

A plugin does not run until you have allowed it. When Elio opens and finds one it has not been
asked about, it shows the plugin's name, its folder and the exact command it would run, and starts
it only if you choose **Allow**; **Not now** leaves it off and asks again next time. The answer is
kept in Elio's encrypted vault and holds for that `plugin.json` in that folder, so a plugin whose
`plugin.json` changes is asked about again. The other files in the folder are not checked: allow a
plugin only if you trust everything in its folder.

## The manifest

```json
{
  "name": "example",
  "description": "Types the date command into the terminal and says so.",
  "version": "1.0.0",
  "command": "python",
  "args": ["plugin.py"],
  "env": { "MY_SETTING": "1" }
}
```

| Field | | |
|---|---|---|
| `command` | required | The program to start. A bare name such as `python` is looked up on the `PATH`, unless a file by that name is in the plugin's folder. A relative path such as `bin/tool` is taken from the plugin's folder. |
| `args` | optional | Arguments, as a list of strings. |
| `env` | optional | Extra environment variables, as an object of strings. |
| `name` | optional | Defaults to the folder's name. Two plugins cannot share a name. |
| `description`, `version` | optional | Shown to the user. |

Comments and trailing commas are allowed in the file.

The process starts in the plugin's folder, so `"args": ["plugin.py"]` finds the script beside the
manifest. Elio also sets `ELIO_PLUGIN_DIR` to that folder, and `PYTHONUNBUFFERED=1` and
`PYTHONIOENCODING=utf-8` unless they are set already.

## The wire

[JSON-RPC 2.0](https://www.jsonrpc.org/specification), one message per line, UTF-8, in both
directions. This is the same framing as the stdio transport of the Model Context Protocol.

- Elio writes to the plugin's standard input and reads its standard output.
- A message must not contain a line break; a line is at most 1 MB.
- **Flush after every line.** A message left in a buffer is a message Elio never got.
- Lines on standard output that are not JSON are ignored and kept in the plugin's log, with
  everything written to standard error. The log keeps the last 200 lines.
- When Elio closes, or the plugin is stopped, the process and anything it started is ended. There
  is no shutdown message: do not keep state that needs one.

Both sides send requests. Number your own requests however you like; they only need to be unique
among the ones you are still waiting on. Elio's requests have integer ids.

## What Elio asks the plugin

### `initialize`

Sent once, right after the process starts. **Answer within 5 seconds** or the plugin is stopped
and reported as broken.

```json
→ {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"elioVersion":"1.0.0.0","protocolVersion":1}}
← {"jsonrpc":"2.0","id":1,"result":{"name":"example","commands":[
     {"id":"date","title":"Show the date","description":"Runs date in the active terminal."}]}}
```

Each command needs an `id` and a `title`; `description` is optional. Commands without both are
skipped.

### `command/run`

The user picked one of the plugin's commands.

```json
→ {"jsonrpc":"2.0","id":2,"method":"command/run","params":{"id":"date","context":{
     "server":{"name":"web-1","address":"10.0.0.5","user":"deploy","kind":"ssh"},
     "directory":"/var/www","selection":"some selected text"}}}
← {"jsonrpc":"2.0","id":2,"result":{}}
```

Every part of `context` is optional and is left out when Elio does not know it. `kind` is one of
`ssh`, `telnet`, `serial` or `local`. Passwords, keys and anything else from the vault are never
part of it, and there is no way to ask for them.

Answer with any `result` when the command is done, or with an `error` to have its `message` shown
to the user:

```json
← {"jsonrpc":"2.0","id":2,"error":{"code":-32000,"message":"No server is selected."}}
```

A command that says nothing for 60 seconds is taken to be stuck, and the plugin is stopped. Time
the user spends answering a `prompt` does not count.

## What the plugin may ask Elio

Only **while one of its commands is running**, between receiving `command/run` and answering it.
At any other time these fail with error `-32001`. Requests are carried out one at a time, in the
order they were sent.

| Method | Params | Result | |
|---|---|---|---|
| `terminal/send` | `{"text": "..."}` | `{}` | Types the text into the active terminal. |
| `terminal/run` | `{"command": "..."}` | `{}` | Types the command and presses Enter. |
| `notify` | `{"message": "..."}` | `{}` | Shows a short message. |
| `clipboard/write` | `{"text": "..."}` | `{}` | Puts text on the clipboard. |
| `openUrl` | `{"url": "https://..."}` | `{}` | Opens a web page. Only `http` and `https`. |
| `prompt` | `{"title": "...", "placeholder": "..."}` | `{"text": "..."}` | Asks the user for a line of text. `text` is `null` if they backed out. `placeholder` is optional. |

```json
← {"jsonrpc":"2.0","id":"a1","method":"prompt","params":{"title":"Branch to deploy"}}
→ {"jsonrpc":"2.0","id":"a1","result":{"text":"main"}}
```

A request without an `id` is carried out and not answered.

### Errors

| Code | |
|---|---|
| `-32601` | There is no such method. |
| `-32602` | A parameter is missing or wrong, or the address given to `openUrl` is not `http` or `https`. |
| `-32000` | Elio could not do it, for example because no terminal is open. The message says why. |
| `-32001` | No command of this plugin is running. |

## When things go wrong

Nothing a plugin does is fatal to Elio. A plugin that cannot be started, answers `initialize`
late or wrongly, exits in the middle of a command, or stops answering is reported to the user by
name, and is started afresh the next time one of its commands is used.
