#!/usr/bin/env node
// Self-authored MCP server (design piece T4) that proxies the Godot client's localhost debug control
// channel (src/Mmo.Client.Godot/DebugControlChannel.cs) as agent tools.
//
// SAFETY MODEL (see docs/client-control-telemetry-design.md):
//   - Connects ONLY to 127.0.0.1 (loopback). The host is hard-coded to a loopback literal and is not
//     configurable, so this process can never reach a remote machine.
//   - No other network access, no shell execution, no filesystem access. The only thing this server
//     does is open a short-lived TCP socket to the local client channel, send one JSON line, read one
//     JSON line back, and close. It is a thin adapter: zero client logic lives here.
//   - The underlying channel only exists when the client was started with MMO_DEBUG_CONTROL_PORT set,
//     binds 127.0.0.1 exclusively, and is absent in shipped builds.
//
// TRANSPORT: stdio. Plain JS (no TS build step). Official @modelcontextprotocol/sdk.
//
// CHANNEL PROTOCOL (must match DebugControlChannel.cs exactly): line-delimited JSON. One
// {"cmd":"..."} request line in, one JSON response line out, then the channel keeps the socket open.
// We open a fresh connection per request (like the T3 client-control.ps1 script), send our line, read
// the first response line, and close — robust against a half-closed socket between calls.

import net from "node:net";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";

// ---- Configuration ---------------------------------------------------------------------------

// Loopback only. Intentionally NOT configurable — see the safety model above.
const CONTROL_HOST = "127.0.0.1";

// How long to wait for the connect + the single response line before giving up. The channel polls
// once per client frame, so a healthy client replies in a few ms; a missing client should fail fast.
const CONNECT_TIMEOUT_MS = 3000;
const RESPONSE_TIMEOUT_MS = 5000;

// Bound the response we are willing to buffer, mirroring the channel's own MaxLineBytes intent so a
// misbehaving local peer cannot make us allocate without limit.
const MAX_RESPONSE_BYTES = 256 * 1024;

function resolvePort() {
  // Priority: --port=<n> / --port <n> arg, then MMO_DEBUG_CONTROL_PORT env (the same value the client
  // was started with). No silent default — a wrong port would just hang against nothing.
  const argv = process.argv.slice(2);
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith("--port=")) {
      return parsePort(a.slice("--port=".length));
    }
    if (a === "--port" && i + 1 < argv.length) {
      return parsePort(argv[i + 1]);
    }
  }

  const env = process.env.MMO_DEBUG_CONTROL_PORT;
  if (env && env.trim().length > 0) {
    return parsePort(env.trim());
  }

  throw new Error(
    "No control port. Pass --port <n> or set MMO_DEBUG_CONTROL_PORT (the same value the client was started with)."
  );
}

function parsePort(raw) {
  const port = Number.parseInt(raw, 10);
  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    throw new Error(`Invalid control port '${raw}'. Expected an integer 1-65535.`);
  }
  return port;
}

// ---- Channel client (one fresh connection per request) ---------------------------------------

// Sends one JSON request object and resolves with the parsed JSON response object from the channel.
// Opens, writes one line, reads the first response line, then closes.
function sendRequest(requestObject) {
  const port = resolvePort();
  const line = JSON.stringify(requestObject) + "\n";

  return new Promise((resolve, reject) => {
    const socket = net.createConnection({ host: CONTROL_HOST, port });
    let buffer = "";
    let settled = false;

    const cleanup = () => {
      socket.removeAllListeners();
      socket.destroy();
    };

    const fail = (message) => {
      if (settled) return;
      settled = true;
      cleanup();
      reject(new Error(message));
    };

    const succeed = (value) => {
      if (settled) return;
      settled = true;
      cleanup();
      resolve(value);
    };

    socket.setTimeout(CONNECT_TIMEOUT_MS);

    socket.once("connect", () => {
      socket.setNoDelay(true);
      // Once connected, the relevant bound is the response wait, not the connect wait.
      socket.setTimeout(RESPONSE_TIMEOUT_MS);
      socket.write(line, "utf8");
    });

    socket.on("data", (chunk) => {
      buffer += chunk.toString("utf8");
      if (Buffer.byteLength(buffer, "utf8") > MAX_RESPONSE_BYTES) {
        fail(`Control channel response exceeded ${MAX_RESPONSE_BYTES} bytes.`);
        return;
      }

      const newlineIndex = buffer.indexOf("\n");
      if (newlineIndex === -1) {
        return; // wait for the full line
      }

      const rawLine = buffer.slice(0, newlineIndex).replace(/\r$/, "");
      try {
        succeed(JSON.parse(rawLine));
      } catch (parseError) {
        fail(`Control channel returned non-JSON line: ${rawLine} (${parseError.message})`);
      }
    });

    socket.once("timeout", () => {
      fail(
        `Timed out talking to control channel at ${CONTROL_HOST}:${port}. ` +
          `Is the client running with MMO_DEBUG_CONTROL_PORT=${port}?`
      );
    });

    socket.once("error", (err) => {
      fail(
        `Cannot reach control channel at ${CONTROL_HOST}:${port}: ${err.message}. ` +
          `Start the client with MMO_DEBUG_CONTROL_PORT set.`
      );
    });

    socket.once("close", () => {
      // Channel closed before we saw a full response line.
      fail(`Control channel at ${CONTROL_HOST}:${port} closed before responding to: ${line.trim()}`);
    });
  });
}

// Wraps a channel round-trip as an MCP tool result. The channel's own JSON (including {"ok":false,...}
// errors) is returned verbatim as pretty-printed text so the agent sees exactly what the client said.
async function callChannel(requestObject) {
  try {
    const response = await sendRequest(requestObject);
    const isError = response && response.ok === false;
    return {
      isError,
      content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
    };
  } catch (err) {
    return {
      isError: true,
      content: [{ type: "text", text: `error: ${err.message}` }],
    };
  }
}

// ---- MCP server + tools ----------------------------------------------------------------------

const server = new McpServer({
  name: "mmo-client-control",
  version: "0.1.0",
});

const DIRECTIONS = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

// Commands (input injection) -------------------------------------------------------------------

server.registerTool(
  "client.move",
  {
    title: "Move the avatar",
    description:
      "Drive the local avatar in one of the 8 tile directions. Optional durationMs auto-stops after that long; omit/0 keeps moving until client.stop.",
    inputSchema: {
      dir: z.enum(DIRECTIONS).describe("Tile direction: N, NE, E, SE, S, SW, W, NW."),
      durationMs: z
        .number()
        .min(0)
        .optional()
        .describe("Optional auto-stop after this many milliseconds. Omit or 0 to keep moving."),
    },
  },
  async ({ dir, durationMs }) => {
    const req = { cmd: "move", dir };
    if (typeof durationMs === "number" && durationMs > 0) {
      req.durationMs = durationMs;
    }
    return callChannel(req);
  }
);

server.registerTool(
  "client.stop",
  {
    title: "Stop movement",
    description: "Stop any movement started by client.move or client.autopilot.",
    inputSchema: {},
  },
  async () => callChannel({ cmd: "stop" })
);

server.registerTool(
  "client.chat",
  {
    title: "Send chat",
    description: "Send a chat message (or a slash command like /who) as the local player.",
    inputSchema: {
      text: z.string().describe("Chat text to send."),
    },
  },
  async ({ text }) => callChannel({ cmd: "chat", text })
);

server.registerTool(
  "client.autopilot",
  {
    title: "Run autopilot",
    description:
      "Start a scripted, repeatable movement loop (for controlled repro/profiling). Returns immediately; the run plays out over subsequent frames and appends per-frame rows to .run/client-frames.csv.",
    inputSchema: {
      pattern: z
        .enum(["square", "line", "zigzag", "circle"])
        .optional()
        .describe("Movement pattern. Defaults to 'square'."),
      durationMs: z
        .number()
        .min(0)
        .optional()
        .describe("How long to run, in milliseconds. Defaults to 30000 (30s)."),
    },
  },
  async ({ pattern, durationMs }) => {
    const req = { cmd: "autopilot" };
    if (pattern) req.pattern = pattern;
    if (typeof durationMs === "number" && durationMs > 0) req.durationMs = durationMs;
    return callChannel(req);
  }
);

server.registerTool(
  "client.toggle_perf",
  {
    title: "Toggle perf HUD",
    description: "Toggle the F3 performance HUD overlay on the client.",
    inputSchema: {},
  },
  async () => callChannel({ cmd: "toggle_perf" })
);

server.registerTool(
  "client.toggle_fullscreen",
  {
    title: "Toggle fullscreen",
    description: "Toggle the client window between fullscreen and windowed.",
    inputSchema: {},
  },
  async () => callChannel({ cmd: "toggle_fullscreen" })
);

// Queries (telemetry readout) ------------------------------------------------------------------

server.registerTool(
  "client.telemetry",
  {
    title: "Read telemetry",
    description:
      "Read live client telemetry: fps, frame ms (last/max), per-_Process-section timing (poll/renderState/entities/camera/overlay), gc gen0/1/2 deltas, hitch count.",
    inputSchema: {},
  },
  async () => callChannel({ cmd: "telemetry" })
);

server.registerTool(
  "client.interp",
  {
    title: "Read interpolation state",
    description:
      "Read movement interpolation debug: queueDepth, effective cadenceMs, confirmedTile, confirmedSnapshotSeq, latencyMs.",
    inputSchema: {},
  },
  async () => callChannel({ cmd: "interp" })
);

server.registerTool(
  "client.entities",
  {
    title: "Read entities",
    description:
      "Read all visible entities: networkId, isLocal, name, authoritative tile, and render x/y per entity.",
    inputSchema: {},
  },
  async () => callChannel({ cmd: "entities" })
);

server.registerTool(
  "client.state",
  {
    title: "Read client state",
    description:
      "Read connection/login state: connection, loggedIn, role, zone, visibleEntities, localTile.",
    inputSchema: {},
  },
  async () => callChannel({ cmd: "state" })
);

server.registerTool(
  "client.ping",
  {
    title: "Ping the channel",
    description: "Prove the control channel is reachable. Returns {\"ok\":true,\"pong\":\"ok\"}.",
    inputSchema: {},
  },
  async () => callChannel({ cmd: "ping" })
);

// ---- Boot ------------------------------------------------------------------------------------

async function main() {
  const transport = new StdioServerTransport();
  await server.connect(transport);
  // Note: do not write to stdout — it is the MCP transport. Diagnostics go to stderr only.
  process.stderr.write("mmo-client-control MCP server ready (stdio).\n");
}

main().catch((err) => {
  process.stderr.write(`mmo-client-control MCP server failed to start: ${err.stack || err}\n`);
  process.exit(1);
});
