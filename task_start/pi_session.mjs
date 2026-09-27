// Pi review session discovery. Uses the same session-report API as Herdr's Pi
// integration, without installing global hooks or reporting agent/pass state.
import net from "node:net";
import path from "node:path";

export default function (pi) {
  const socketPath = process.env.HERDR_SOCKET_PATH;
  const paneId = process.env.HERDR_PANE_ID;
  if (process.env.HERDR_ENV !== "1" || !socketPath || !paneId) return;
  const endpoint = process.platform === "win32" ? `\\\\.\\pipe\\${socketPath}` : socketPath;
  let sequence = Date.now() * 1000;

  async function report(event, ctx) {
    if (ctx.mode !== "tui") return; // Headless preflight is not the reviewer.
    const sessionPath = ctx.sessionManager.getSessionFile();
    if (typeof sessionPath !== "string" || !path.isAbsolute(sessionPath)) return;
    // Pi allocates this path before flushing its first assistant message. Report
    // the native reference now; Python verifies header identity/history separately.
    const request = {
      id: `task:pi-session:${++sequence}`,
      method: "pane.report_agent_session",
      params: {
        pane_id: paneId,
        source: "herdr:pi",
        agent: "pi",
        seq: sequence,
        agent_session_path: sessionPath,
        ...(event.type === "session_start" ? { session_start_source: event.reason } : {}),
      },
    };
    for (const timeoutMs of [500, 1500]) {
      const delivered = await new Promise((resolve) => {
        let reply = "";
        let finished = false;
        const socket = net.createConnection(endpoint);
        const finish = (ok) => {
          if (finished) return;
          finished = true;
          clearTimeout(timer);
          socket.destroy();
          resolve(ok);
        };
        const timer = setTimeout(() => finish(false), timeoutMs);
        socket.on("error", () => finish(false));
        socket.on("end", () => finish(false));
        socket.on("connect", () => socket.write(`${JSON.stringify(request)}\n`));
        socket.on("data", (chunk) => {
          reply += chunk.toString();
          if (!reply.includes("\n")) return;
          try {
            const response = JSON.parse(reply.split("\n", 1)[0]);
            finish(response.id === request.id && !response.error && !!response.result);
          } catch {
            finish(false);
          }
        });
      });
      if (delivered) return;
    }
    // No invented fallback: later lifecycle events retry the real reference.
  }

  for (const event of ["session_start", "agent_start", "agent_settled", "session_shutdown"]) {
    pi.on(event, report);
  }
}
