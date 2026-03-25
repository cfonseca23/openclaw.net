import { createInterface } from "readline";

let _writeLock = false;
const _writeQueue = [];

function flushQueue() {
  if (_writeLock || _writeQueue.length === 0) return;
  _writeLock = true;
  const line = _writeQueue.shift();
  process.stdout.write(line + "\n", () => {
    _writeLock = false;
    flushQueue();
  });
}

function writeLine(obj) {
  _writeQueue.push(JSON.stringify(obj));
  flushQueue();
}

export function sendResponse(id, result) {
  writeLine({ id, result: result ?? null });
}

export function sendError(id, code, message) {
  writeLine({ id, error: { code, message } });
}

export function sendNotification(type, params) {
  writeLine({ notification: type, params });
}

export function readRequests(handler) {
  const rl = createInterface({ input: process.stdin, terminal: false });

  rl.on("line", async (line) => {
    const trimmed = line.trim();
    if (!trimmed) return;
    if (trimmed === "__shutdown__") {
      rl.close();
      return;
    }

    let request;
    try {
      request = JSON.parse(trimmed);
    } catch {
      sendError("unknown", -32700, "Parse error");
      return;
    }

    try {
      const result = await handler(request);
      sendResponse(request.id ?? "unknown", result);
    } catch (err) {
      sendError(request.id ?? "unknown", -1, err?.message ?? "Unknown error");
    }

    if (request.method === "shutdown") {
      rl.close();
    }
  });

  rl.on("close", () => {
    process.exit(0);
  });
}
