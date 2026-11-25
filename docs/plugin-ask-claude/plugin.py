"""Ask Claude what the output in front of you means. An Elio plugin; see docs/PLUGINS.md for the protocol.

It takes the selected text, or else what the last command printed, shows how much of it would go to Anthropic, and
sends it only when you say so. The answer comes back in a window. Your API key is read the way the Anthropic SDK
always reads it: ANTHROPIC_API_KEY, or the profile `ant auth login` keeps. Nothing about it passes through Elio.
"""
import json
import sys
import threading

try:
    import anthropic
except ImportError:  # Reported when the command runs, so that Elio can still list the plugin.
    anthropic = None

MODEL = "claude-opus-5-5"

# A screenful or two is what a question is about; a longer text is a log, and its end is what matters.
LONGEST_TEXT = 100_000

# Elio takes a command that says nothing for a minute to be stuck, and Claude may take longer than that to answer.
PING_EVERY = 15

next_id = 0


def send(message):
    message["jsonrpc"] = "2.0"
    sys.stdout.write(json.dumps(message) + "\n")
    sys.stdout.flush()


def read():
    line = sys.stdin.readline()
    if not line:
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


def instructions(directory):
    where = f" The terminal is in the folder {directory}." if directory else ""
    return (
        "You help someone who works in a terminal. They show you some of its output: what their last command printed, "
        "or text they selected. Say briefly what it means. If something went wrong, say what most likely caused it and "
        "how to fix it, with the exact commands to run. Keep it short: a few paragraphs at most, plain text, no headings. "
        "Answer in the language the question is written in, or in English when there is no question." + where
    )


def ask(text, question, directory):
    client = anthropic.Anthropic()
    prompt = f"Terminal output:\n{text}"
    if question:
        prompt += f"\n\nMy question: {question}"

    response = client.beta.messages.create(
        model=MODEL,
        max_tokens=16000,
        # A question about some output is a short exchange; little thinking answers it well.
        output_config={"effort": "low"},
        # A request the model declines for safety is answered by another model rather than not at all.
        betas=["server-side-fallback-2026-07-01"],
        fallbacks="default",
        system=instructions(directory),
        messages=[{"role": "user", "content": prompt}],
    )
    if response.stop_reason == "refusal":
        return "Claude did not answer this question."
    answer = "\n\n".join(block.text for block in response.content if block.type == "text").strip()
    return answer or "Claude gave no answer."


def ask_in_background(text, question, directory):
    """Asks on a thread of its own and tells Elio every few seconds that the command is still at work."""
    outcome = {}

    def work():
        try:
            outcome["answer"] = ask(text, question, directory)
        except anthropic.AuthenticationError:
            outcome["error"] = "Anthropic did not accept the API key. Set ANTHROPIC_API_KEY, or run `ant auth login`, then start Elio again."
        except anthropic.RateLimitError:
            outcome["error"] = "Anthropic says too many requests were made just now. Try again in a minute."
        except anthropic.APIStatusError as error:
            outcome["error"] = f"Anthropic answered with an error ({error.status_code}): {error.message}"
        except anthropic.APIConnectionError:
            outcome["error"] = "Anthropic could not be reached. Check the internet connection."
        except anthropic.AnthropicError as error:
            # Most often: no key at all.
            outcome["error"] = f"Claude could not be asked: {error}. Set ANTHROPIC_API_KEY, or run `ant auth login`."

    worker = threading.Thread(target=work, daemon=True)
    worker.start()
    while worker.is_alive():
        worker.join(PING_EVERY)
        if worker.is_alive():
            call("ping", {})
    return outcome


def run_command(command_id, context):
    if command_id != "ask":
        raise RuntimeError("Unknown command: " + command_id)
    if anthropic is None:
        raise RuntimeError("The Anthropic SDK for Python is not installed. Run: pip install anthropic")

    text = context.get("selection") or (call("terminal/lastOutput", {}) or {}).get("text")
    if not text:
        raise RuntimeError("Select some text in the terminal first, or choose \"Mark commands in this shell\" so the last output can be found.")
    if len(text) > LONGEST_TEXT:
        text = "[...]\n" + text[-LONGEST_TEXT:]

    # Nothing leaves this computer before the user has seen how much would, and said yes.
    question = call("prompt", {
        "title": f"Send {len(text):,} characters of terminal output to Anthropic and ask Claude about them?",
        "placeholder": "Your question (optional)",
    }).get("text")
    if question is None:
        return

    outcome = ask_in_background(text, question.strip(), context.get("directory"))
    if "error" in outcome:
        raise RuntimeError(outcome["error"])
    call("show", {"title": "Claude", "text": outcome["answer"]})


while True:
    request = read()
    method = request.get("method")
    params = request.get("params") or {}

    if method == "initialize":
        send({"id": request["id"], "result": {
            "name": "ask-claude",
            "commands": [
                {"id": "ask", "title": "Ask Claude about this output",
                 "description": "Sends the selected text, or the last command's output, to Claude after you say yes."},
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
