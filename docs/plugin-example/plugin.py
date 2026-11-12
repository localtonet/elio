"""A minimal Elio plugin. See docs/PLUGINS.md for the protocol.

Elio starts this script, writes one JSON-RPC message per line to its standard input and reads
one per line from its standard output. Anything written to standard error ends up in the plugin's log.
"""
import json
import sys

next_id = 0


def send(message):
    message["jsonrpc"] = "2.0"
    sys.stdout.write(json.dumps(message) + "\n")
    # Without this the line sits in a buffer and Elio waits for an answer that never leaves.
    sys.stdout.flush()


def read():
    line = sys.stdin.readline()
    if not line:
        # Elio closed the pipe: time to go.
        sys.exit(0)
    return json.loads(line)


def call(method, params):
    """Asks Elio to do something and waits for its answer."""
    global next_id
    next_id += 1
    send({"id": next_id, "method": method, "params": params})
    while True:
        message = read()
        if message.get("id") == next_id and "method" not in message:
            if "error" in message:
                raise RuntimeError(message["error"]["message"])
            return message.get("result")


def run_command(command_id, context):
    if command_id == "date":
        call("terminal/run", {"command": "date"})
        server = (context.get("server") or {}).get("name") or "this terminal"
        call("notify", {"message": "Asked " + server + " for the date."})
    else:
        raise RuntimeError("Unknown command: " + command_id)


while True:
    request = read()
    method = request.get("method")
    params = request.get("params") or {}

    if method == "initialize":
        send({"id": request["id"], "result": {
            "name": "example",
            "commands": [
                {"id": "date", "title": "Show the date", "description": "Runs date in the active terminal."},
            ],
        }})
    elif method == "command/run":
        try:
            run_command(params.get("id"), params.get("context") or {})
            send({"id": request["id"], "result": {}})
        except Exception as error:
            send({"id": request["id"], "error": {"code": -32000, "message": str(error)}})
    elif "id" in request:
        send({"id": request["id"], "error": {"code": -32601, "message": "Unknown method: " + str(method)}})
