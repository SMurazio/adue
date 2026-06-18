import * as THREE from "/vendor/three.module.js";

const state = {
  socket: null,
  entities: [],
  name: "",
  role: "Player",
  tick: null,
  snapshotSequence: null,
  connected: false,
  snapshotIsComplete: true,
  snapshotTotalEntities: 0,
  selfNetworkId: null
};

const els = {
  name: document.querySelector("#name"),
  host: document.querySelector("#host"),
  port: document.querySelector("#port"),
  connect: document.querySelector("#connect"),
  disconnect: document.querySelector("#disconnect"),
  status: document.querySelector("#status"),
  world: document.querySelector("#world"),
  tick: document.querySelector("#tick"),
  entityCount: document.querySelector("#entity-count"),
  entities: document.querySelector("#entities"),
  chatLog: document.querySelector("#chat-log"),
  chatForm: document.querySelector("#chat-form"),
  chatInput: document.querySelector("#chat-input"),
  metricsPanel: document.querySelector("#metrics-panel"),
  metricsToggle: document.querySelector("#metrics-toggle"),
  metricsGrid: document.querySelector("#metrics-grid"),
  metricsMessages: document.querySelector("#metrics-messages"),
  metricsUpdated: document.querySelector("#metrics-updated")
};

const keysDown = new Set();
let lastMoveKey = "";
let metricsTimer = null;
const metricsCollapsedKey = "mmo.metricsCollapsed";
const playerNameKey = "mmo.playerName";
let rightMouseActive = false;
let rightMouseTarget = null;
let rightMousePointer = null;
let lastRightMoveSentAt = 0;
const snapshotInterpolationDelayMs = 150;
const maxEntitySnapshotBuffer = 8;
const cameraFollowAlpha = 0.14;
const serverMoveUnitsPerSecond = 5;
const localCorrectionAlpha = 0.18;
const minCameraZoom = 0.55;
const maxCameraZoom = 3.25;
const debugVisibilityRadius = 96;
const entityStaleAfterMs = 250;
const entityExpireAfterMs = 1500;
const partialSnapshotGhostOpacity = 0.42;
const currentMoveVector = { x: 0, y: 0 };
let lastFrameAt = performance.now();
let cameraZoom = 1;
let scene = null;
let camera = null;
let renderer = null;
let worldRoot = null;
let cameraFocus = new THREE.Vector3(0, 0, 0);
let desiredFocus = new THREE.Vector3(0, 0, 0);
const meshes = new Map();
const entityRegistry = new Map();
let raycaster = null;
let pointer = null;
let groundPlane = null;
let ground = null;
let grid = null;
let moveMarker = null;
let visibilityRing = null;
let resizeObserver = null;
const renderSize = {
  width: 0,
  height: 0,
  pixelRatio: 0,
  zoom: 0
};
const metricsLines = {
  state: "",
  live: "",
  minute: "",
  total: "",
  messages: ""
};
let snapshotAssembly = null;

function setStatus(text) {
  els.status.textContent = text;
}

function readMetricsCollapsedPreference() {
  try {
    return window.localStorage.getItem(metricsCollapsedKey) === "true";
  } catch {
    return false;
  }
}

function writeMetricsCollapsedPreference(collapsed) {
  try {
    window.localStorage.setItem(metricsCollapsedKey, collapsed ? "true" : "false");
  } catch {
  }
}

function loadSavedPlayerName() {
  try {
    const savedName = window.localStorage.getItem(playerNameKey)?.trim();
    if (savedName) {
      els.name.value = savedName;
    }
  } catch {
  }
}

function savePlayerNamePreference(value) {
  const name = value.trim();
  try {
    if (name) {
      window.localStorage.setItem(playerNameKey, name);
    } else {
      window.localStorage.removeItem(playerNameKey);
    }
  } catch {
  }
}

function setMetricsCollapsed(collapsed) {
  els.metricsPanel.classList.toggle("is-collapsed", collapsed);
  els.metricsToggle.setAttribute("aria-expanded", collapsed ? "false" : "true");
  els.metricsToggle.setAttribute("aria-label", collapsed ? "Expand metrics" : "Collapse metrics");
}

function toggleMetricsCollapsed() {
  const collapsed = !els.metricsPanel.classList.contains("is-collapsed");
  setMetricsCollapsed(collapsed);
  writeMetricsCollapsedPreference(collapsed);
}

function initScene() {
  try {
    scene = new THREE.Scene();
    scene.background = new THREE.Color(0x0c0f12);
    scene.fog = new THREE.Fog(0x0c0f12, 42, 95);

    camera = new THREE.OrthographicCamera(-10, 10, 10, -10, 0.1, 200);
    renderer = new THREE.WebGLRenderer({ antialias: true });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
    renderer.setClearColor(0x0c0f12);
    els.world.append(renderer.domElement);

    worldRoot = new THREE.Group();
    scene.add(worldRoot);

    raycaster = new THREE.Raycaster();
    pointer = new THREE.Vector2();
    groundPlane = new THREE.Plane(new THREE.Vector3(0, 1, 0), 0);

    ground = new THREE.Mesh(
      new THREE.PlaneGeometry(2000, 2000),
      new THREE.MeshStandardMaterial({ color: 0x182127, roughness: 0.95, metalness: 0.0 })
    );
    ground.rotation.x = -Math.PI / 2;
    ground.position.y = -0.03;
    ground.receiveShadow = true;
    worldRoot.add(ground);

    grid = new THREE.GridHelper(2000, 500, 0x31414d, 0x202a31);
    grid.position.y = 0.01;
    worldRoot.add(grid);

    visibilityRing = new THREE.Mesh(
      new THREE.RingGeometry(0.9985, 1.0015, 128),
      new THREE.MeshBasicMaterial({
        color: 0x8bdcff,
        transparent: true,
        opacity: 0.55,
        side: THREE.DoubleSide,
        depthWrite: false
      })
    );
    visibilityRing.rotation.x = -Math.PI / 2;
    visibilityRing.position.y = 0.07;
    visibilityRing.visible = false;
    worldRoot.add(visibilityRing);

    moveMarker = new THREE.Group();
    const moveMarkerRing = new THREE.Mesh(
      new THREE.RingGeometry(0.42, 0.55, 32),
      new THREE.MeshBasicMaterial({ color: 0x91d68b, transparent: true, opacity: 0.95, side: THREE.DoubleSide })
    );
    moveMarkerRing.rotation.x = -Math.PI / 2;
    moveMarkerRing.position.y = 0.06;
    moveMarker.add(moveMarkerRing);

    const moveMarkerDot = new THREE.Mesh(
      new THREE.CylinderGeometry(0.08, 0.08, 0.08, 14),
      new THREE.MeshBasicMaterial({ color: 0x91d68b })
    );
    moveMarkerDot.position.y = 0.08;
    moveMarker.add(moveMarkerDot);
    moveMarker.visible = false;
    worldRoot.add(moveMarker);

    const ambient = new THREE.HemisphereLight(0xdce7f0, 0x1a2026, 1.8);
    scene.add(ambient);

    const sun = new THREE.DirectionalLight(0xffffff, 2.2);
    sun.position.set(16, 26, 14);
    scene.add(sun);

    bindPointerMovement();
    bindResponsiveCanvas();
    resizeRenderer(true);
    animate();
  } catch (error) {
    renderer = null;
    els.world.classList.add("world-fallback");
    els.world.textContent = "3D view unavailable. Connection, chat, and entity list still work.";
    setStatus(`3D unavailable: ${error.message}`);
  }
}

function bindResponsiveCanvas() {
  if (!renderer) {
    return;
  }

  if ("ResizeObserver" in window) {
    resizeObserver = new ResizeObserver(() => resizeRenderer(true));
    resizeObserver.observe(els.world);
  }

  if (window.visualViewport) {
    window.visualViewport.addEventListener("resize", () => resizeRenderer(true));
  }
}

function connect() {
  if (state.socket && state.socket.readyState === WebSocket.OPEN) {
    return;
  }

  state.name = els.name.value.trim() || `WebPlayer${Math.floor(Math.random() * 9000) + 1000}`;
  els.name.value = state.name;
  savePlayerNamePreference(state.name);
  const host = encodeURIComponent(els.host.value.trim() || "127.0.0.1");
  const port = encodeURIComponent(els.port.value.trim() || "7777");
  const name = encodeURIComponent(state.name);
  const scheme = location.protocol === "https:" ? "wss" : "ws";

  state.socket = new WebSocket(`${scheme}://${location.host}/ws?host=${host}&port=${port}&name=${name}`);
  setStatus("Connecting");

  state.socket.addEventListener("open", () => {
    state.connected = true;
    els.connect.disabled = true;
    els.disconnect.disabled = false;
  });

  state.socket.addEventListener("close", () => {
    state.connected = false;
    state.role = "Player";
    stopMetricsPolling();
    els.connect.disabled = false;
    els.disconnect.disabled = true;
    setStatus("Disconnected");
  });

  state.socket.addEventListener("message", event => handleMessage(JSON.parse(event.data)));
}

function disconnect() {
  send({ type: "quit" });
  stopMetricsPolling();
  state.socket?.close();
}

function send(message) {
  if (state.socket && state.socket.readyState === WebSocket.OPEN) {
    state.socket.send(JSON.stringify(message));
  }
}

function handleMessage(message) {
  switch (message.type) {
    case "status":
      setStatus(message.text);
      break;
    case "serverHello":
      setStatus(`${message.serverName} - ${message.tickRate} ticks/sec`);
      break;
    case "login":
      state.role = message.role ?? "Player";
      setStatus(message.accepted ? `Logged in as ${message.displayName} (${state.role})` : `Login rejected: ${message.reason}`);
      if (message.accepted && state.role === "Admin") {
        startMetricsPolling();
      } else {
        stopMetricsPolling();
      }
      break;
    case "snapshot":
      handleSnapshotMessage(message);
      break;
    case "entitySpawn":
      handleEntitySpawn(message);
      break;
    case "entityDespawn":
      handleEntityDespawn(message);
      break;
    case "chat":
      if (message.sender === "server" && handleMetricsLine(message.text)) {
        break;
      }
      addChat(message.sender, message.text);
      break;
    case "error":
      setStatus(`${message.code}: ${message.message}`);
      break;
  }
}

function handleEntityDespawn(message) {
  if (state.tick !== null && message.tick !== undefined && message.tick < state.tick) {
    return;
  }

  const id = message.networkId;
  state.entities = state.entities.filter(entity => entity.id !== id);

  const entry = meshes.get(id);
  if (entry) {
    removeEntity(id, entry);
  }

  renderEntities();
  els.entityCount.textContent = state.snapshotIsComplete
    ? `${state.entities.length} entities`
    : `${state.entities.length}/${state.snapshotTotalEntities} entities`;
}

function handleEntitySpawn(message) {
  const id = message.networkId;
  const entity = {
    id,
    characterId: message.characterId,
    kind: message.kind ?? "Player",
    name: message.name ?? `Entity ${id}`,
    x: message.x ?? 0,
    y: message.y ?? 0
  };

  entityRegistry.set(id, entity);
  if (entity.name === state.name) {
    state.selfNetworkId = id;
  }

  const entry = meshes.get(id);
  if (entry && entry.name !== entity.name) {
    entry.name = entity.name;
    replaceEntityLabel(entry, entity.name);
  }
}

function addChat(sender, text) {
  const line = document.createElement("div");
  line.className = "chat-line";
  const safeSender = document.createElement("span");
  safeSender.className = "sender";
  safeSender.textContent = sender;
  line.append(safeSender, document.createTextNode(` ${text}`));
  els.chatLog.append(line);
  els.chatLog.scrollTop = els.chatLog.scrollHeight;
}

function handleSnapshotMessage(message) {
  const chunkCount = Math.max(1, message.chunkCount ?? 1);
  const chunkIndex = message.chunkIndex ?? 0;
  const totalEntities = message.totalEntities ?? message.entities.length;
  const sequence = message.sequence ?? message.tick;

  if (state.tick !== null && message.tick < state.tick) {
    return;
  }

  if (chunkCount === 1) {
    snapshotAssembly = null;
    applySnapshot(message.tick, sequence, totalEntities, message.isComplete ?? true, message.entities);
    return;
  }

  if (chunkIndex < 0 || chunkIndex >= chunkCount) {
    return;
  }

  if (
    !snapshotAssembly ||
    snapshotAssembly.tick !== message.tick ||
    snapshotAssembly.sequence !== sequence ||
    snapshotAssembly.chunkCount !== chunkCount
  ) {
    snapshotAssembly = {
      tick: message.tick,
      sequence,
      totalEntities,
      isComplete: message.isComplete ?? false,
      chunkCount,
      chunks: Array.from({ length: chunkCount }, () => null)
    };
  }

  snapshotAssembly.totalEntities = totalEntities;
  snapshotAssembly.isComplete = snapshotAssembly.isComplete && (message.isComplete ?? false);
  snapshotAssembly.chunks[chunkIndex] = message.entities;

  if (snapshotAssembly.chunks.every(Boolean)) {
    const entities = snapshotAssembly.chunks.flat();
    applySnapshot(snapshotAssembly.tick, snapshotAssembly.sequence, snapshotAssembly.totalEntities, snapshotAssembly.isComplete, entities);
    snapshotAssembly = null;
  }
}

function applySnapshot(tick, sequence, totalEntities, isComplete, entities) {
  state.tick = tick;
  state.snapshotSequence = sequence;
  state.entities = hydrateSnapshotEntities(entities);
  state.snapshotIsComplete = isComplete;
  state.snapshotTotalEntities = totalEntities;
  updateSceneEntities(tick, sequence);
  renderEntities();
  els.tick.textContent = `tick ${state.tick ?? "-"} / seq ${state.snapshotSequence ?? "-"}`;
  els.entityCount.textContent = state.snapshotIsComplete
    ? `${state.entities.length} entities`
    : `${state.entities.length}/${state.snapshotTotalEntities} entities`;
}

function hydrateSnapshotEntities(states) {
  return states.map(entity => {
    const id = entity.id;
    const known = entityRegistry.get(id);
    return {
      id,
      characterId: known?.characterId ?? "",
      kind: known?.kind ?? "Player",
      name: known?.name ?? `Entity ${id}`,
      x: entity.x,
      y: entity.y
    };
  });
}

function startMetricsPolling() {
  stopMetricsPolling();
  if (state.role !== "Admin") {
    return;
  }

  els.metricsPanel.hidden = false;
  requestMetrics();
  metricsTimer = window.setInterval(requestMetrics, 1000);
}

function stopMetricsPolling() {
  if (metricsTimer !== null) {
    window.clearInterval(metricsTimer);
    metricsTimer = null;
  }

  els.metricsPanel.hidden = true;
}

function requestMetrics() {
  if (!state.connected || state.role !== "Admin") {
    return;
  }

  send({ type: "chat", text: "/metrics" });
}

function handleMetricsLine(text) {
  if (typeof text !== "string") {
    return false;
  }

  if (text.startsWith("metrics state:")) {
    metricsLines.state = text;
    renderMetricsPanel();
    return true;
  }

  if (text.startsWith("metrics 5s:")) {
    metricsLines.live = text;
    renderMetricsPanel();
    return true;
  }

  if (text.startsWith("metrics 60s:")) {
    metricsLines.minute = text;
    renderMetricsPanel();
    return true;
  }

  if (text.startsWith("metrics total:")) {
    metricsLines.total = text;
    renderMetricsPanel();
    return true;
  }

  if (text.startsWith("metrics:")) {
    metricsLines.total = text.replace("metrics:", "metrics total:");
    renderMetricsPanel();
    return true;
  }

  if (text.startsWith("message metrics:")) {
    metricsLines.messages = text.replace(/^message metrics:\s*/, "");
    renderMetricsPanel();
    return true;
  }

  return false;
}

function renderMetricsPanel() {
  const stateLine = metricsLines.state;
  const liveLine = metricsLines.live;
  const minuteLine = metricsLines.minute;
  const totalLine = metricsLines.total;
  const rows = [
    createMetricSection("State"),
    createMetric("Peers / players", `${readMetric(stateLine, /peers=(\d+)/)} / ${readMetric(stateLine, /players=(\d+)/)}`),
    createMetric("Tick", readMetric(stateLine, /tick=(\d+)/)),
    createMetric("Uptime", readMetric(stateLine, /uptime=([^,]+)/)),
    createMetric("Stress", readMetric(stateLine, /(stress .*)$/), true),

    createMetricSection("Live 5s"),
    createMetric("Tick/s", readMetric(liveLine, /tick\/s=([^,]+)/)),
    createMetric("Tick ms avg/max", readMetric(liveLine, /tickMs avg\/max=([^,]+)/)),
    createMetric("Snapshots/s", readMetric(liveLine, /snap\/s=([^,]+)/)),
    createMetric("Move/s", readMetric(liveLine, /move\/s=([^,]+)/)),
    createMetric("Visible avg/max", readMetric(liveLine, /visible avg\/max=([^,]+)/)),
    createMetric("Bandwidth", `${readMetric(liveLine, /out=([^,]+)/)} out / ${readMetric(liveLine, /in=([^,]+)/)} in`),
    createMetric("Messages/s", `${readMetric(liveLine, /recv\/s=([^,]+)/)} in / ${readMetric(liveLine, /sent\/s=([^,]+)/)} out`),
    createMetric("Culled/s", readMetric(liveLine, /culled\/s=([^,]+)/)),
    createMetric("Faults/s", `${readMetric(liveLine, /sendFail\/s=([^,]+)/)} send / ${readMetric(liveLine, /bad\/s=([^,]+)/)} bad / ${readMetric(liveLine, /netErr\/s=([^,]+)/)} net`, true),

    createMetricSection("Last 60s"),
    createMetric("Tick ms avg/max", readMetric(minuteLine, /tickMs avg\/max=([^,]+)/)),
    createMetric("Snapshots/s", readMetric(minuteLine, /snap\/s=([^,]+)/)),
    createMetric("Move/s", readMetric(minuteLine, /move\/s=([^,]+)/)),
    createMetric("Visible avg/max", readMetric(minuteLine, /visible avg\/max=([^,]+)/)),
    createMetric("Culled/s", readMetric(minuteLine, /culled\/s=([^,]+)/)),
    createMetric("Bandwidth", `${readMetric(minuteLine, /out=([^,]+)/)} out / ${readMetric(minuteLine, /in=([^,]+)/)} in`),

    createMetricSection("Total"),
    createMetric("Tick ms last/avg/max", readMetric(totalLine, /tickMs last\/avg\/max=([^,]+)/), true),
    createMetric("Avg snapshots/s", readMetric(totalLine, /snap\/s\(avg\)=([^,]+)/)),
    createMetric("Snapshots", readMetric(totalLine, /snapshots=(\d+)/)),
    createMetric("Avg bandwidth", `${readMetric(totalLine, /outAvg=([^,]+)/)} out / ${readMetric(totalLine, /inAvg=([^,]+)/)} in`),
    createMetric("Fault totals", `${readMetric(totalLine, /sendFail=(\d+)/)} send / ${readMetric(totalLine, /badPackets=(\d+)/)} bad / ${readMetric(totalLine, /netErr=(\d+)/)} net`, true),
    createMetric("Login total", `${readMetric(totalLine, /login=([^,]+)/)} (${readMetric(totalLine, /loginMs avg\/max=([^,]+)/)})`, true)
  ];

  els.metricsGrid.replaceChildren(...rows);
  els.metricsPanel.hidden = false;
  els.metricsMessages.textContent = metricsLines.messages;
  els.metricsUpdated.textContent = `updated ${new Date().toLocaleTimeString()}`;
}

function createMetric(label, value, wide = false) {
  const item = document.createElement("div");
  item.className = wide ? "metric metric-wide" : "metric";

  const labelNode = document.createElement("div");
  labelNode.className = "label";
  labelNode.textContent = label;

  const valueNode = document.createElement("div");
  valueNode.className = "value";
  valueNode.textContent = value || "-";

  item.append(labelNode, valueNode);
  return item;
}

function createMetricSection(text) {
  const section = document.createElement("div");
  section.className = "metric-section";
  section.textContent = text;
  return section;
}

function readMetric(line, pattern) {
  return line.match(pattern)?.[1]?.trim() ?? "-";
}

function updateSceneEntities(tick, sequence) {
  const self = findSelfEntity(state.entities);
  if (self) {
    desiredFocus.set(self.x, 0, self.y);
  }

  if (!worldRoot) {
    return;
  }

  for (const entry of meshes.values()) {
    entry.inLatestSnapshot = false;
  }

  const now = performance.now();
  for (const entity of state.entities) {
    const entry = getOrCreateEntity(entity);
    entry.to.set(entity.x, 0, entity.y);
    entry.lastSnapshotAt = now;
    entry.lastSeenAt = now;
    entry.inLatestSnapshot = true;
    if (entry.name !== entity.name) {
      entry.name = entity.name;
      replaceEntityLabel(entry, entity.name);
    }
    entry.isSelf = isSelfEntity(entity);
    if (entry.isSelf) {
      entry.serverPosition.set(entity.x, 0, entity.y);
      if (!entry.localInitialized) {
        entry.renderPosition.copy(entry.serverPosition);
        entry.localInitialized = true;
      }
    } else {
      addEntitySnapshotSample(entry, tick, sequence, entity.x, entity.y, now);
    }
    setEntityColor(entry, entry.isSelf);
  }
}

function addEntitySnapshotSample(entry, tick, sequence, x, y, receivedAt) {
  const last = entry.snapshotBuffer.at(-1);
  if (last && last.sequence === sequence) {
    last.tick = tick;
    last.x = x;
    last.y = y;
    last.receivedAt = receivedAt;
  } else {
    entry.snapshotBuffer.push({ tick, sequence, x, y, receivedAt });
  }

  while (entry.snapshotBuffer.length > maxEntitySnapshotBuffer) {
    entry.snapshotBuffer.shift();
  }
}

function findSelfEntity(entities) {
  return entities.find(isSelfEntity);
}

function isSelfEntity(entity) {
  return state.selfNetworkId !== null
    ? entity.id === state.selfNetworkId
    : entity.name === state.name;
}

function getOrCreateEntity(entity) {
  const existing = meshes.get(entity.id);
  if (existing) {
    return existing;
  }

  const group = new THREE.Group();
  group.position.set(entity.x, 0, entity.y);

  const shadow = new THREE.Mesh(
    new THREE.CylinderGeometry(0.48, 0.48, 0.025, 32),
    new THREE.MeshBasicMaterial({ color: 0x050708, transparent: true, opacity: 0.42 })
  );
  shadow.position.y = 0.012;
  group.add(shadow);

  const body = new THREE.Mesh(
    new THREE.CylinderGeometry(0.27, 0.34, 0.9, 18),
    new THREE.MeshStandardMaterial({ color: 0x7ec8f1, roughness: 0.75, transparent: true })
  );
  body.position.y = 0.48;
  group.add(body);

  const head = new THREE.Mesh(
    new THREE.SphereGeometry(0.24, 18, 12),
    new THREE.MeshStandardMaterial({ color: 0xdce8f0, roughness: 0.6, transparent: true })
  );
  head.position.y = 1.08;
  group.add(head);

  const ring = new THREE.Mesh(
    new THREE.RingGeometry(0.52, 0.63, 32),
    new THREE.MeshBasicMaterial({ color: 0x7ec8f1, transparent: true, opacity: 0.9, side: THREE.DoubleSide })
  );
  ring.rotation.x = -Math.PI / 2;
  ring.position.y = 0.04;
  group.add(ring);

  const label = createLabel(entity.name);
  label.position.set(0, 1.65, 0);
  group.add(label);

  worldRoot.add(group);

  const entry = {
    group,
    shadow,
    body,
    head,
    ring,
    label,
    from: new THREE.Vector3(entity.x, 0, entity.y),
    to: new THREE.Vector3(entity.x, 0, entity.y),
    renderPosition: new THREE.Vector3(entity.x, 0, entity.y),
    serverPosition: new THREE.Vector3(entity.x, 0, entity.y),
    lastSnapshotAt: performance.now(),
    lastSeenAt: performance.now(),
    snapshotBuffer: [],
    name: entity.name,
    isSelf: false,
    inLatestSnapshot: true,
    localInitialized: false
  };
  meshes.set(entity.id, entry);
  return entry;
}

function setEntityColor(entry, isSelf) {
  entry.body.material.color.setHex(isSelf ? 0x7ec8f1 : 0xe6be65);
  entry.ring.visible = isSelf;
}

function updateEntityFreshness(id, entry, nowMs) {
  if (entry.isSelf) {
    setEntityOpacity(entry, 1);
    entry.label.visible = true;
    return true;
  }

  if (!entry.inLatestSnapshot) {
    entry.label.visible = false;
  }

  const ageMs = nowMs - entry.lastSeenAt;
  if (ageMs <= entityStaleAfterMs) {
    setEntityOpacity(entry, 1);
    entry.label.visible = entry.inLatestSnapshot;
    return true;
  }

  if (!state.snapshotIsComplete) {
    setEntityOpacity(entry, partialSnapshotGhostOpacity);
    entry.label.visible = false;
    return true;
  }

  if (ageMs > entityExpireAfterMs) {
    removeEntity(id, entry);
    return false;
  }

  const fade = 1 - ((ageMs - entityStaleAfterMs) / (entityExpireAfterMs - entityStaleAfterMs));
  setEntityOpacity(entry, THREE.MathUtils.clamp(fade, 0.18, 1));
  entry.label.visible = false;
  return true;
}

function setEntityOpacity(entry, opacity) {
  entry.body.material.opacity = opacity;
  entry.head.material.opacity = opacity;
  entry.shadow.material.opacity = 0.42 * opacity;
  entry.label.material.opacity = opacity;
}

function removeEntity(id, entry) {
  worldRoot.remove(entry.group);
  entry.group.traverse(child => {
    if (child.geometry) {
      child.geometry.dispose();
    }

    if (child.material) {
      disposeMaterial(child.material);
    }
  });
  meshes.delete(id);
}

function disposeMaterial(material) {
  if (Array.isArray(material)) {
    for (const item of material) {
      disposeMaterial(item);
    }
    return;
  }

  if (material.map) {
    material.map.dispose();
  }

  material.dispose();
}

function createLabel(text) {
  const canvas = document.createElement("canvas");
  canvas.width = 256;
  canvas.height = 64;
  const context = canvas.getContext("2d");
  context.fillStyle = "rgba(10, 14, 18, 0.72)";
  context.fillRect(0, 0, canvas.width, canvas.height);
  context.strokeStyle = "rgba(126, 200, 241, 0.72)";
  context.strokeRect(1, 1, canvas.width - 2, canvas.height - 2);
  context.fillStyle = "#e8edf2";
  context.font = "28px Segoe UI, Arial, sans-serif";
  context.textAlign = "center";
  context.textBaseline = "middle";
  context.fillText(text.slice(0, 18), canvas.width / 2, canvas.height / 2);

  const texture = new THREE.CanvasTexture(canvas);
  const material = new THREE.SpriteMaterial({ map: texture, transparent: true, depthTest: false });
  const sprite = new THREE.Sprite(material);
  sprite.scale.set(2.7, 0.68, 1);
  return sprite;
}

function replaceEntityLabel(entry, text) {
  entry.group.remove(entry.label);
  if (entry.label.material.map) {
    entry.label.material.map.dispose();
  }
  entry.label.material.dispose();

  const label = createLabel(text);
  label.position.set(0, 1.65, 0);
  entry.label = label;
  entry.group.add(label);
}

function renderEntities() {
  els.entities.replaceChildren(...state.entities.map(entity => {
    const row = document.createElement("div");
    row.className = "entity";

    const name = document.createElement("div");
    name.textContent = entity.name;

    const pos = document.createElement("div");
    pos.className = "pos";
    pos.textContent = `${entity.x.toFixed(1)}, ${entity.y.toFixed(1)}`;

    row.append(name, pos);
    return row;
  }));
}

function sendMoveDirection(direction) {
  rightMouseActive = false;
  rightMousePointer = null;
  if (moveMarker) {
    moveMarker.visible = false;
  }
  sendScreenMove(direction);
}

function sendMoveVector(x, y) {
  if (Math.abs(x) < 0.0001) {
    x = 0;
  }

  if (Math.abs(y) < 0.0001) {
    y = 0;
  }

  x = Number(x.toFixed(4));
  y = Number(y.toFixed(4));
  currentMoveVector.x = x;
  currentMoveVector.y = y;

  const key = `${x},${y}`;
  if (key === lastMoveKey) {
    return;
  }

  lastMoveKey = key;
  send({ type: "move", x, y });
}

function sendScreenMove(direction) {
  const input = screenInputFromDirection(direction);
  const world = screenInputToWorldVector(input.x, input.y);
  sendMoveVector(world.x, world.y);
}

function screenInputFromDirection(direction) {
  switch (direction) {
    case "nw":
      return { x: -1, y: 1 };
    case "w":
      return { x: 0, y: 1 };
    case "ne":
      return { x: 1, y: 1 };
    case "a":
      return { x: -1, y: 0 };
    case "d":
      return { x: 1, y: 0 };
    case "sw":
      return { x: -1, y: -1 };
    case "s":
      return { x: 0, y: -1 };
    case "se":
      return { x: 1, y: -1 };
    default:
      return { x: 0, y: 0 };
  }
}

function screenInputToWorldVector(screenX, screenY) {
  if (screenX === 0 && screenY === 0) {
    return { x: 0, y: 0 };
  }

  if (!camera) {
    const fallbackX = (screenX * 0.70710678) + (screenY * -0.70710678);
    const fallbackY = (screenX * -0.70710678) + (screenY * -0.70710678);
    return normalize2(fallbackX, fallbackY);
  }

  camera.updateMatrixWorld();
  const screenRight = new THREE.Vector3().setFromMatrixColumn(camera.matrixWorld, 0);
  const screenUp = new THREE.Vector3().setFromMatrixColumn(camera.matrixWorld, 1);
  screenRight.y = 0;
  screenUp.y = 0;

  if (screenRight.lengthSq() < 0.0001 || screenUp.lengthSq() < 0.0001) {
    return { x: 0, y: 0 };
  }

  screenRight.normalize();
  screenUp.normalize();
  const world = screenRight.multiplyScalar(screenX).add(screenUp.multiplyScalar(screenY));
  return normalize2(world.x, world.z);
}

function normalize2(x, y) {
  const length = Math.hypot(x, y);
  if (length < 0.0001) {
    return { x: 0, y: 0 };
  }

  return { x: x / length, y: y / length };
}

function syncKeyboardMovement() {
  if (rightMouseActive) {
    return;
  }

  const screenX = (keysDown.has("d") ? 1 : 0) + (keysDown.has("a") ? -1 : 0);
  const screenY = (keysDown.has("w") ? 1 : 0) + (keysDown.has("s") ? -1 : 0);
  const world = screenInputToWorldVector(screenX, screenY);
  sendMoveVector(world.x, world.y);
}

for (const button of document.querySelectorAll("[data-move]")) {
  button.addEventListener("click", () => {
    lastMoveKey = "";
    sendMoveDirection(button.dataset.move);
  });
}

document.addEventListener("keydown", event => {
  if (event.target instanceof HTMLInputElement) {
    return;
  }

  const key = event.key.toLowerCase();
  if (["w", "a", "s", "d"].includes(key)) {
    event.preventDefault();
    keysDown.add(key);
    syncKeyboardMovement();
  }

  if (event.key === " ") {
    event.preventDefault();
    keysDown.clear();
    rightMouseActive = false;
    rightMousePointer = null;
    if (moveMarker) {
      moveMarker.visible = false;
    }
    sendMoveVector(0, 0);
  }
});

document.addEventListener("keyup", event => {
  if (event.target instanceof HTMLInputElement) {
    return;
  }

  const key = event.key.toLowerCase();
  if (["w", "a", "s", "d"].includes(key)) {
    event.preventDefault();
    keysDown.delete(key);
    syncKeyboardMovement();
  }
});

function bindPointerMovement() {
  if (!renderer) {
    return;
  }

  renderer.domElement.addEventListener("contextmenu", event => {
    event.preventDefault();
  });

  renderer.domElement.addEventListener("wheel", event => {
    event.preventDefault();
    const delta = normalizeWheelDelta(event);
    const factor = Math.exp(-delta * 0.0012);
    cameraZoom = THREE.MathUtils.clamp(cameraZoom * factor, minCameraZoom, maxCameraZoom);
    resizeRenderer(true);
  }, { passive: false });

  renderer.domElement.addEventListener("pointerdown", event => {
    if (event.button !== 2) {
      return;
    }

    event.preventDefault();
    renderer.domElement.setPointerCapture(event.pointerId);
    keysDown.clear();
    rightMouseActive = true;
    rememberRightMousePointer(event);
    updateRightMouseTargetFromPointer();
    syncRightMouseMovement(true);
  });

  renderer.domElement.addEventListener("pointermove", event => {
    if (!rightMouseActive) {
      return;
    }

    event.preventDefault();
    rememberRightMousePointer(event);
  });

  renderer.domElement.addEventListener("pointerup", event => {
    if (event.button === 2) {
      stopRightMouseMovement(event.pointerId);
    }
  });

  renderer.domElement.addEventListener("pointercancel", event => {
    stopRightMouseMovement(event.pointerId);
  });
}

function normalizeWheelDelta(event) {
  if (event.deltaMode === WheelEvent.DOM_DELTA_LINE) {
    return event.deltaY * 16;
  }

  if (event.deltaMode === WheelEvent.DOM_DELTA_PAGE) {
    return event.deltaY * Math.max(1, renderSize.height);
  }

  return event.deltaY;
}

window.addEventListener("blur", () => {
  keysDown.clear();
  rightMouseActive = false;
  rightMousePointer = null;
  if (moveMarker) {
    moveMarker.visible = false;
  }
  sendMoveVector(0, 0);
});

function stopRightMouseMovement(pointerId) {
  if (renderer && pointerId !== undefined && renderer.domElement.hasPointerCapture(pointerId)) {
    renderer.domElement.releasePointerCapture(pointerId);
  }

  rightMouseActive = false;
  rightMouseTarget = null;
  rightMousePointer = null;
  if (moveMarker) {
    moveMarker.visible = false;
  }
  sendMoveVector(0, 0);
}

function rememberRightMousePointer(event) {
  rightMousePointer = {
    clientX: event.clientX,
    clientY: event.clientY
  };
}

function updateRightMouseTargetFromPointer() {
  if (!renderer || !camera || !raycaster || !pointer || !groundPlane) {
    return;
  }

  if (!rightMousePointer) {
    return;
  }

  const rect = renderer.domElement.getBoundingClientRect();
  pointer.x = ((rightMousePointer.clientX - rect.left) / rect.width) * 2 - 1;
  pointer.y = -(((rightMousePointer.clientY - rect.top) / rect.height) * 2 - 1);
  raycaster.setFromCamera(pointer, camera);

  const point = new THREE.Vector3();
  if (raycaster.ray.intersectPlane(groundPlane, point)) {
    rightMouseTarget = point;
    moveMarker.position.set(point.x, 0, point.z);
    moveMarker.visible = true;
  }
}

function syncRightMouseMovement(force = false) {
  if (!rightMouseActive || !rightMouseTarget) {
    return;
  }

  const now = performance.now();
  if (!force && now - lastRightMoveSentAt < 50) {
    return;
  }

  const self = findSelfEntity(state.entities);
  if (!self) {
    return;
  }

  const dx = rightMouseTarget.x - self.x;
  const dy = rightMouseTarget.z - self.y;
  const distance = Math.hypot(dx, dy);
  if (distance < 0.35) {
    sendMoveVector(0, 0);
    return;
  }

  lastRightMoveSentAt = now;
  sendMoveVector(dx / distance, dy / distance);
}

function sampleEntityPosition(entry, nowMs) {
  if (entry.isSelf) {
    return entry.renderPosition;
  }

  const interpolated = interpolateRemoteEntityPosition(entry.snapshotBuffer, nowMs - snapshotInterpolationDelayMs);
  if (interpolated) {
    entry.renderPosition.set(interpolated.x, 0, interpolated.y);
  }

  return entry.renderPosition;
}

function interpolateRemoteEntityPosition(samples, renderTimeMs) {
  if (samples.length === 0) {
    return null;
  }

  if (samples.length === 1 || renderTimeMs <= samples[0].receivedAt) {
    return samples[0];
  }

  for (let i = 1; i < samples.length; i++) {
    const previous = samples[i - 1];
    const next = samples[i];
    if (renderTimeMs <= next.receivedAt) {
      const span = Math.max(1, next.receivedAt - previous.receivedAt);
      const alpha = THREE.MathUtils.clamp((renderTimeMs - previous.receivedAt) / span, 0, 1);
      return {
        x: THREE.MathUtils.lerp(previous.x, next.x, alpha),
        y: THREE.MathUtils.lerp(previous.y, next.y, alpha)
      };
    }
  }

  return samples.at(-1);
}

function advanceLocalPlayer(deltaSeconds) {
  if (currentMoveVector.x === 0 && currentMoveVector.y === 0) {
    return;
  }

  const self = [...meshes.values()].find(entry => entry.isSelf);
  if (!self) {
    return;
  }

  self.renderPosition.x += currentMoveVector.x * serverMoveUnitsPerSecond * deltaSeconds;
  self.renderPosition.z += currentMoveVector.y * serverMoveUnitsPerSecond * deltaSeconds;
}

function reconcileLocalPlayers() {
  for (const entry of meshes.values()) {
    if (entry.isSelf) {
      entry.renderPosition.lerp(entry.serverPosition, localCorrectionAlpha);
    }
  }
}

function updateDebugVisibilityRing() {
  if (!visibilityRing) {
    return;
  }

  const self = [...meshes.values()].find(entry => entry.isSelf);
  if (!self) {
    visibilityRing.visible = false;
    return;
  }

  visibilityRing.position.set(self.renderPosition.x, 0.07, self.renderPosition.z);
  visibilityRing.scale.setScalar(debugVisibilityRadius);
  visibilityRing.visible = true;
}

function updateCameraFocusTargetFromSelf() {
  const self = [...meshes.values()].find(entry => entry.isSelf);
  if (self) {
    desiredFocus.copy(self.renderPosition);
  }
}

function resizeRenderer(force = false) {
  if (!renderer || !camera) {
    return;
  }

  const rect = els.world.getBoundingClientRect();
  const width = Math.max(1, Math.round(rect.width));
  const height = Math.max(1, Math.round(rect.height));
  const pixelRatio = Math.min(Math.max(window.devicePixelRatio || 1, 1), 2);

  if (
    !force &&
    renderSize.width === width &&
    renderSize.height === height &&
    renderSize.pixelRatio === pixelRatio &&
    renderSize.zoom === cameraZoom
  ) {
    return;
  }

  renderSize.width = width;
  renderSize.height = height;
  renderSize.pixelRatio = pixelRatio;
  renderSize.zoom = cameraZoom;

  renderer.setPixelRatio(pixelRatio);
  renderer.setSize(width, height, false);

  const targetPixelsPerWorldUnit = width < 700 ? 34 : 42;
  const baseViewHeight = THREE.MathUtils.clamp(height / targetPixelsPerWorldUnit, 10, 22);
  const viewHeight = THREE.MathUtils.clamp(baseViewHeight / cameraZoom, 4.5, 34);
  const aspect = width / height;
  camera.left = (-viewHeight * aspect) / 2;
  camera.right = (viewHeight * aspect) / 2;
  camera.top = viewHeight / 2;
  camera.bottom = -viewHeight / 2;
  camera.updateProjectionMatrix();
}

function animate() {
  if (!renderer || !scene || !camera || !ground || !grid) {
    return;
  }

  requestAnimationFrame(animate);

  resizeRenderer();
  cameraFocus.lerp(desiredFocus, cameraFollowAlpha);

  const distance = 18;
  camera.position.set(cameraFocus.x + distance, 15, cameraFocus.z + distance);
  camera.lookAt(cameraFocus.x, 0, cameraFocus.z);
  camera.updateMatrixWorld();

  const nowMs = performance.now();
  const deltaSeconds = Math.min((nowMs - lastFrameAt) / 1000, 0.05);
  lastFrameAt = nowMs;
  const now = nowMs * 0.001;
  advanceLocalPlayer(deltaSeconds);
  reconcileLocalPlayers();
  updateCameraFocusTargetFromSelf();
  updateDebugVisibilityRing();
  updateRightMouseTargetFromPointer();
  syncRightMouseMovement();

  for (const [id, entry] of [...meshes]) {
    if (!updateEntityFreshness(id, entry, nowMs)) {
      continue;
    }

    const position = sampleEntityPosition(entry, nowMs);
    entry.group.position.copy(position);
    entry.body.rotation.y += 0.015;
    entry.head.position.y = 1.08 + Math.sin(now * 3 + entry.renderPosition.x) * 0.025;
    entry.label.lookAt(camera.position);
  }

  renderer.render(scene, camera);
}

els.connect.addEventListener("click", connect);
els.disconnect.addEventListener("click", disconnect);
els.metricsToggle.addEventListener("click", toggleMetricsCollapsed);
els.name.addEventListener("change", () => savePlayerNamePreference(els.name.value));
els.name.addEventListener("blur", () => savePlayerNamePreference(els.name.value));

els.chatForm.addEventListener("submit", event => {
  event.preventDefault();
  const text = els.chatInput.value.trim();
  if (text.length > 0) {
    send({ type: "chat", text });
    els.chatInput.value = "";
  }
});

window.addEventListener("resize", () => resizeRenderer(true));
loadSavedPlayerName();
setMetricsCollapsed(readMetricsCollapsedPreference());
initScene();
