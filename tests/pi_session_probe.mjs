// Offline Pi lifecycle/Herdr protocol probe: no model, credentials, or real pane.
import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import fs from "node:fs";
import net from "node:net";
import path from "node:path";
import { pathToFileURL } from "node:url";

const [extension, directory, nativeManagerModule] = process.argv.slice(2);
const cwd = path.join(directory, "checkout");
const sessions = path.join(directory, "sessions");
fs.mkdirSync(cwd, { recursive: true });
fs.mkdirSync(sessions, { recursive: true });
let manager;
if (nativeManagerModule) {
  // Optional local validation with the installed Pi SessionManager, no agent run.
  const { SessionManager } = await import(pathToFileURL(nativeManagerModule));
  manager = SessionManager.create(cwd, sessions);
} else {
  // Pi 0.87.1 allocates identity/path in memory; disk persistence starts with
  // the first assistant message. A process/title observation exposes neither.
  const id = randomUUID();
  const file = path.join(sessions, `${Date.now()}_${id}.jsonl`);
  const entries = [{ type: "session", version: 3, id, cwd }];
  manager = {
    getSessionFile: () => file,
    getSessionId: () => id,
    appendMessage(message) {
      entries.push({ type: "message", message });
      if (entries.some((e) => e.message?.role === "assistant")) {
        fs.writeFileSync(file, entries.map((e) => JSON.stringify(e)).join("\n") + "\n");
      }
    },
  };
}

const requests = [];
const socketPath = process.platform === "win32" ? `task-pi-test-${process.pid}` : path.join(directory, "herdr.sock");
const endpoint = process.platform === "win32" ? `\\\\.\\pipe\\${socketPath}` : socketPath;
const server = net.createServer((socket) => {
  let input = "";
  socket.on("data", (chunk) => {
    input += chunk.toString();
    if (!input.includes("\n")) return;
    const request = JSON.parse(input.split("\n", 1)[0]);
    requests.push(request);
    socket.end(JSON.stringify({ id: request.id, result: { type: "agent_session_reported" } }) + "\n");
  });
});
await new Promise((resolve) => server.listen(endpoint, resolve));
process.env.HERDR_ENV = "1";
process.env.HERDR_SOCKET_PATH = socketPath;
process.env.HERDR_PANE_ID = "review-pane";
let handlers;
if (nativeManagerModule) {
  const { loadExtensions } = await import(pathToFileURL(path.join(path.dirname(nativeManagerModule), "extensions/loader.js")));
  const loaded = await loadExtensions([extension], cwd);
  assert.deepEqual(loaded.errors, []);
  assert.equal(loaded.extensions.length, 1);
  handlers = new Map([...loaded.extensions[0].handlers].map(([event, registered]) => [event, registered[0]]));
} else {
  const { default: extensionFactory } = await import(pathToFileURL(extension));
  handlers = new Map();
  extensionFactory({ on: (event, handler) => handlers.set(event, handler) });
}
const emit = (type, mode = "tui", sessionManager = manager, reason = "startup") =>
  handlers.get(type)({ type, reason }, { mode, sessionManager });

await emit("session_start", "rpc");
await emit("session_start", "tui", { getSessionFile: () => undefined });
await emit("session_start", "tui", { getSessionFile: () => "relative/not-a-session" });
assert.equal(requests.length, 0); // No guessed reference or preflight process report.
await emit("session_start");
const existedAtStartup = fs.existsSync(manager.getSessionFile());
assert.equal(existedAtStartup, false);
await emit("agent_start");
manager.appendMessage({ role: "user", content: "Controlled probe", timestamp: Date.now() });
assert.equal(fs.existsSync(manager.getSessionFile()), false);
manager.appendMessage({ role: "assistant", content: [{ type: "text", text: "Controlled result" }], timestamp: Date.now() });
await emit("agent_settled");
await emit("session_shutdown", "tui", manager, "quit");
const retainedReports = requests.slice();
await emit("session_start", "tui", { getSessionFile: () => path.join(sessions, "different.jsonl") }, "new");
assert.equal(requests.at(-1).params.agent_session_path, path.join(sessions, "different.jsonl"));
await new Promise((resolve) => server.close(resolve));
// An unavailable socket is bounded and produces no inferred identity.
await emit("session_start");
console.log(JSON.stringify({ cwd, sessionFile: manager.getSessionFile(), sessionId: manager.getSessionId(),
  existedAtStartup, retainedReports, switchedReport: requests.at(-1), events: [...handlers.keys()] }));
