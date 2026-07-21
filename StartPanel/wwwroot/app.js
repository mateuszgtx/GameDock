let currentState = "unknown";
let currentSystemId = null;
let canRestart = false;
let canSwitchSystem = false;
let configuredSystems = [];
let actionInProgress = false;
let statusRequestInProgress = false;
let metricsRequestInProgress = false;
let statusTimer = null;
let switchingSystemId = null;
let pendingOperation = null;
let pendingOperationTimer = null;
let operationFailed = false;

const OPERATION_TIMEOUT_MS = 30_000;
const RESTART_MINIMUM_WAIT_MS = 8_000;

const actionMessage = document.querySelector("#actionMessage");
const powerButton = document.querySelector("#powerButton");
const restartButton = document.querySelector("#restartButton");
const resetControls = document.querySelector(".reset-controls");
const statusDot = document.querySelector("#statusDot");
const statusText = document.querySelector("#statusText");
const actionHint = document.querySelector("#actionHint");
const systemsSection = document.querySelector("#systemsSection");
const systemButtons = document.querySelector("#systemButtons");
const systemHint = document.querySelector("#systemHint");
const dashboardLayout = document.querySelector(".dashboard-layout");
const metricsSection = document.querySelector("#metricsSection");
const capacityBadge = document.querySelector("#capacityBadge");
const metricsMessage = document.querySelector("#metricsMessage");
const wolfSection = document.querySelector("#wolfSection");
const wolfSessionBadge = document.querySelector("#wolfSessionBadge");
const wolfFreeSlots = document.querySelector("#wolfFreeSlots");
const wolfSessionList = document.querySelector("#wolfSessionList");
const wolfMessage = document.querySelector("#wolfMessage");

const metricElements = {
  cpu: metricElement("cpu"),
  gpu: metricElement("gpu"),
  ram: metricElement("ram"),
  vram: metricElement("vram"),
  disk: metricElement("disk"),
  rx: metricElement("rx"),
  tx: metricElement("tx")
};

function metricElement(prefix) {
  return {
    card: document.querySelector(`#${prefix}Metric`),
    value: document.querySelector(`#${prefix}Value`),
    bar: document.querySelector(`#${prefix}Bar`),
    detail: document.querySelector(`#${prefix}Detail`)
  };
}

async function api(path, options = {}) {
  const response = await fetch(path, {
    cache: "no-store",
    ...options
  });

  if (!response.ok) {
    let message = `HTTP ${response.status}`;
    const text = await response.text();

    if (text) {
      try {
        const problem = JSON.parse(text);
        message = problem.message ?? problem.detail ?? problem.title ?? message;
      } catch {
        message = text;
      }
    }

    throw new Error(message);
  }

  if (response.status === 204) return null;

  const contentType = response.headers.get("content-type") ?? "";
  return contentType.includes("application/json")
    ? response.json()
    : response.text();
}

function setMessage(text, isError = false) {
  actionMessage.textContent = text;
  actionMessage.classList.toggle("error", isError);
  actionMessage.hidden = !text;
}

function isBusy() {
  return actionInProgress || pendingOperation !== null;
}

function operationLabel(type) {
  if (type === "wake") return "Uruchamianie…";
  if (type === "switch") return "Przełączanie systemu…";
  return "Restartowanie…";
}

function showPendingOperation() {
  if (!pendingOperation) return;

  statusDot.className = "status-dot working";
  statusText.textContent = operationLabel(pendingOperation.type);
  powerButton.className = "power-button working";
  powerButton.setAttribute("aria-label", operationLabel(pendingOperation.type));
  powerButton.removeAttribute("title");
  actionHint.hidden = true;

  const restartWorking = pendingOperation.type === "restart"
    || pendingOperation.type === "switch";
  restartButton.classList.toggle("working", restartWorking);
}

function beginPendingOperation(type, targetSystemId = null) {
  if (pendingOperationTimer !== null) {
    window.clearTimeout(pendingOperationTimer);
  }

  operationFailed = false;
  const startedAt = Date.now();

  pendingOperation = {
    type,
    targetSystemId,
    startedAt,
    deadline: startedAt + OPERATION_TIMEOUT_MS,
    sawOffline: false
  };

  pendingOperationTimer = window.setTimeout(() => {
    if (pendingOperation) {
      failPendingOperation();
    }
  }, OPERATION_TIMEOUT_MS + 100);

  showPendingOperation();
  hideMetrics();
  updateButtons();
}

function clearPendingOperation() {
  if (pendingOperationTimer !== null) {
    window.clearTimeout(pendingOperationTimer);
    pendingOperationTimer = null;
  }

  pendingOperation = null;
  switchingSystemId = null;
  restartButton.classList.remove("working");
}

function failPendingOperation(message = "Nie wykryto komputera lub systemu w ciągu 30 sekund.") {
  clearPendingOperation();
  operationFailed = true;

  statusDot.className = "status-dot failed";
  statusText.textContent = "Nie wykryto";
  powerButton.className = "power-button failed";
  powerButton.setAttribute("aria-label", "Spróbuj ponownie");
  powerButton.setAttribute("title", "Spróbuj ponownie");
  setMessage(message, true);

  updateButtons();
}

function pendingOperationFinished(status) {
  if (!pendingOperation) return false;

  const now = Date.now();
  const elapsed = now - pendingOperation.startedAt;
  const detectedSystem = status.online && Boolean(status.currentSystemId);

  if (status.state === "offline") {
    pendingOperation.sawOffline = true;
  }

  if (pendingOperation.type === "wake") {
    return detectedSystem;
  }

  if (pendingOperation.type === "switch") {
    return detectedSystem
      && status.currentSystemId === pendingOperation.targetSystemId
      && (pendingOperation.sawOffline || elapsed >= RESTART_MINIMUM_WAIT_MS);
  }

  return detectedSystem
    && (pendingOperation.sawOffline || elapsed >= RESTART_MINIMUM_WAIT_MS);
}

function evaluatePendingOperation(status) {
  if (!pendingOperation) return false;

  if (pendingOperationFinished(status)) {
    clearPendingOperation();
    operationFailed = false;
    setMessage("");
    return false;
  }

  if (Date.now() >= pendingOperation.deadline) {
    failPendingOperation();
    return true;
  }

  showPendingOperation();
  updateButtons();
  return true;
}

function updateButtons() {
  const knownState = currentState === "online" || currentState === "offline";
  const online = currentState === "online";
  const restartAvailable = online && canRestart;
  const systemSwitchAvailable = online && canSwitchSystem;
  const busy = isBusy();

  powerButton.disabled = busy || !knownState;

  restartButton.disabled = busy || !restartAvailable;
  resetControls.classList.toggle("locked", !restartAvailable && !pendingOperation);
  resetControls.setAttribute(
    "aria-disabled",
    String(!restartAvailable || busy));

  systemsSection.classList.toggle(
    "locked",
    !systemSwitchAvailable && !pendingOperation);
  systemsSection.setAttribute(
    "aria-disabled",
    String(!systemSwitchAvailable || busy));

  document.querySelectorAll(".system-button").forEach(button => {
    const isCurrent = button.dataset.systemId === currentSystemId;
    button.disabled = busy
      || !systemSwitchAvailable
      || isCurrent;
  });
}

function renderSystems(status) {
  configuredSystems = Array.isArray(status.systems) ? status.systems : [];
  currentSystemId = status.currentSystemId ?? null;
  canRestart = Boolean(status.canRestart);
  canSwitchSystem = Boolean(status.canSwitchSystem);

  systemsSection.hidden = configuredSystems.length === 0;
  if (configuredSystems.length === 0) return;

  const signature = configuredSystems
    .map(system => `${system.id}:${system.name}`)
    .join("|");

  if (systemButtons.dataset.signature !== signature) {
    systemButtons.dataset.signature = signature;
    systemButtons.replaceChildren();

    for (const system of configuredSystems) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "system-button";
      button.dataset.systemId = system.id;
      button.textContent = system.name;
      button.addEventListener("click", () => switchSystem(system));
      systemButtons.append(button);
    }
  }

  document.querySelectorAll(".system-button").forEach(button => {
    const active = button.dataset.systemId === currentSystemId;
    const switching = button.dataset.systemId === switchingSystemId;
    button.classList.toggle("active", active);
    button.classList.toggle("switching", switching);
    button.setAttribute("aria-pressed", String(active));
  });

  systemHint.textContent = "";
  systemHint.hidden = true;

  updateButtons();
}

function renderNormalStatus(status) {
  statusDot.className = `status-dot ${status.state}`;
  powerButton.className = `power-button ${status.state}`;
  restartButton.classList.remove("working");

  if (status.online) {
    statusText.textContent = status.roundtripTimeMs == null
      ? "Online"
      : `Online · ${status.roundtripTimeMs} ms`;

    actionHint.textContent = "";
    actionHint.hidden = true;
    powerButton.setAttribute("aria-label", "Wyłącz komputer");
    powerButton.setAttribute("title", "Wyłącz komputer");
  } else if (status.state === "offline") {
    statusText.textContent = "Offline";
    actionHint.textContent = "";
    actionHint.hidden = true;
    powerButton.setAttribute("aria-label", "Włącz komputer");
    powerButton.setAttribute("title", "Włącz komputer");
  } else {
    statusText.textContent = "Brak połączenia";
    actionHint.hidden = true;
    actionHint.textContent = "";
    powerButton.setAttribute("aria-label", "Stan komputera jest nieznany");
    powerButton.removeAttribute("title");
  }
}

function renderStatus(status) {
  currentState = status.state;
  renderSystems(status);

  if (evaluatePendingOperation(status)) {
    return;
  }

  // Jeżeli system pojawi się później niż po 30 sekundach, panel sam wróci
  // do prawidłowego koloru bez konieczności odświeżania strony.
  if (operationFailed && status.online && status.currentSystemId) {
    operationFailed = false;
    setMessage("");
  }

  if (operationFailed) {
    statusDot.className = "status-dot failed";
    statusText.textContent = "Nie wykryto";
    powerButton.className = "power-button failed";
    powerButton.setAttribute("aria-label", "Spróbuj ponownie");
    powerButton.setAttribute("title", "Spróbuj ponownie");
    updateButtons();
    return;
  }

  renderNormalStatus(status);
  updateButtons();
}

function clampPercent(value) {
  if (!Number.isFinite(value)) return null;
  return Math.max(0, Math.min(100, value));
}

function ratioPercent(used, total) {
  if (!Number.isFinite(used) || !Number.isFinite(total) || total <= 0) {
    return null;
  }

  return clampPercent((used / total) * 100);
}

function formatBytes(value) {
  if (!Number.isFinite(value) || value < 0) return "—";

  const gib = value / (1024 ** 3);
  if (gib >= 10) return `${gib.toFixed(0)} GB`;
  return `${gib.toFixed(1)} GB`;
}

function renderMetric(element, percent, detail) {
  const normalized = clampPercent(percent);
  element.value.textContent = normalized == null
    ? "—"
    : `${Math.round(normalized)}%`;
  element.bar.style.width = normalized == null ? "0%" : `${normalized}%`;
  element.detail.textContent = detail || "—";

  element.card.classList.remove("moderate", "busy", "unavailable");
  if (normalized == null) {
    element.card.classList.add("unavailable");
  } else if (normalized >= 85) {
    element.card.classList.add("busy");
  } else if (normalized >= 60) {
    element.card.classList.add("moderate");
  }
}

function formatNetworkRate(bytesPerSecond) {
  if (!Number.isFinite(bytesPerSecond) || bytesPerSecond < 0) return "—";

  const bitsPerSecond = bytesPerSecond * 8;
  if (bitsPerSecond >= 1_000_000_000) {
    return `${(bitsPerSecond / 1_000_000_000).toFixed(2)} Gb/s`;
  }
  if (bitsPerSecond >= 1_000_000) {
    return `${(bitsPerSecond / 1_000_000).toFixed(1)} Mb/s`;
  }
  if (bitsPerSecond >= 1_000) {
    return `${(bitsPerSecond / 1_000).toFixed(0)} Kb/s`;
  }
  return `${Math.round(bitsPerSecond)} b/s`;
}

function renderNetworkMetric(
  element,
  bytesPerSecond,
  linkSpeedBitsPerSecond,
  detail) {
  const validRate = Number.isFinite(bytesPerSecond) && bytesPerSecond >= 0;
  const percentage = validRate
    && Number.isFinite(linkSpeedBitsPerSecond)
    && linkSpeedBitsPerSecond > 0
      ? clampPercent((bytesPerSecond * 8 / linkSpeedBitsPerSecond) * 100)
      : null;

  element.value.textContent = validRate
    ? formatNetworkRate(bytesPerSecond)
    : "—";
  element.bar.style.width = percentage == null ? "0%" : `${percentage}%`;
  element.detail.textContent = detail || "—";
  element.card.classList.remove("moderate", "busy", "unavailable");

  if (!validRate) {
    element.card.classList.add("unavailable");
  } else if (percentage != null && percentage >= 85) {
    element.card.classList.add("busy");
  } else if (percentage != null && percentage >= 60) {
    element.card.classList.add("moderate");
  }
}

function renderWolf(wolf) {
  if (!wolf?.enabled) {
    wolfSection.hidden = true;
    return;
  }

  wolfSection.hidden = false;
  wolfSection.classList.toggle("unavailable", !wolf.available);
  wolfSessionBadge.classList.remove("full");
  wolfSessionList.replaceChildren();

  if (!wolf.available) {
    wolfSessionBadge.textContent = "—";
    wolfFreeSlots.textContent = "—";
    wolfMessage.textContent = wolf.message || "Wolf nie odpowiada.";
    return;
  }

  const active = Math.max(0, Number(wolf.activeSessions) || 0);
  const maximum = Math.max(0, Number(wolf.maxSessions) || 0);
  const free = Math.max(0, Number(wolf.freeSlots) || 0);

  wolfSessionBadge.textContent = maximum > 0
    ? `${active} / ${maximum}`
    : String(active);
  wolfSessionBadge.classList.toggle("full", maximum > 0 && active >= maximum);
  wolfFreeSlots.textContent = String(free);
  wolfMessage.textContent = active === 0 ? "Brak aktywnych sesji" : "";

  const sessions = Array.isArray(wolf.sessions) ? wolf.sessions : [];
  sessions.forEach((session, index) => {
    const item = document.createElement("div");
    item.className = "wolf-session-item";

    const dot = document.createElement("span");
    dot.className = "wolf-session-dot";

    const content = document.createElement("div");
    const title = document.createElement("strong");
    const detail = document.createElement("span");

    title.textContent = session.application
      ? String(session.application)
      : `Sesja ${index + 1}`;

    const detailParts = [];
    if (session.client) detailParts.push(String(session.client));
    if (session.sessionId) detailParts.push(`#${session.sessionId}`);
    detail.textContent = detailParts.join(" · ");

    content.append(title, detail);
    item.append(dot, content);
    wolfSessionList.append(item);
  });
}

function setMetricsPanelVisible(visible) {
  metricsSection.hidden = !visible;
  dashboardLayout.classList.toggle("single-panel", !visible);
}

function hideMetrics() {
  setMetricsPanelVisible(false);
}

function renderMetricsUnavailable(message) {
  setMetricsPanelVisible(true);
  metricsSection.classList.add("unavailable");
  capacityBadge.className = "capacity-badge unknown";
  capacityBadge.textContent = "Brak danych";
  metricsMessage.textContent = message || "Agent metryk nie odpowiada.";

  Object.values(metricElements).forEach(element => {
    renderMetric(element, null, "—");
  });

  wolfSection.hidden = true;
}

function renderMetrics(metrics) {
  if (currentState !== "online" || pendingOperation) {
    hideMetrics();
    return;
  }

  if (!metrics?.available) {
    renderMetricsUnavailable(metrics?.message);
    return;
  }

  setMetricsPanelVisible(true);
  metricsSection.classList.remove("unavailable");
  metricsMessage.textContent = "";

  const gpu = Array.isArray(metrics.gpus) && metrics.gpus.length > 0
    ? metrics.gpus[0]
    : null;

  const ramPercent = ratioPercent(
    metrics.memoryUsedBytes,
    metrics.memoryTotalBytes);
  const vramPercent = ratioPercent(
    gpu?.memoryUsedBytes,
    gpu?.memoryTotalBytes);
  const diskPercent = ratioPercent(
    metrics.diskUsedBytes,
    metrics.diskTotalBytes);

  const cpuDetail = metrics.cpuTemperatureCelsius == null
    ? "Procesor AMD"
    : `Procesor AMD · ${Math.round(metrics.cpuTemperatureCelsius)}°C`;

  renderMetric(
    metricElements.cpu,
    metrics.cpuPercent,
    cpuDetail);

  const gpuDetailParts = [];
  if (gpu?.temperatureCelsius != null) {
    gpuDetailParts.push(`${Math.round(gpu.temperatureCelsius)}°C`);
  }
  if (gpu?.powerWatts != null) {
    gpuDetailParts.push(`${Math.round(gpu.powerWatts)} W`);
  }

  renderMetric(
    metricElements.gpu,
    gpu?.utilizationPercent,
    gpuDetailParts.length ? gpuDetailParts.join(" · ") : (gpu?.name ?? "Brak danych GPU"));

  renderMetric(
    metricElements.ram,
    ramPercent,
    `${formatBytes(metrics.memoryUsedBytes)} / ${formatBytes(metrics.memoryTotalBytes)}`);

  renderMetric(
    metricElements.vram,
    vramPercent,
    gpu
      ? `${formatBytes(gpu.memoryUsedBytes)} / ${formatBytes(gpu.memoryTotalBytes)}`
      : "Brak danych GPU");

  renderMetric(
    metricElements.disk,
    diskPercent,
    `${formatBytes(metrics.diskUsedBytes)} / ${formatBytes(metrics.diskTotalBytes)}`);

  const networkInterfaces = Array.isArray(metrics.networkInterfaces)
    ? metrics.networkInterfaces.filter(Boolean).join(", ")
    : "";
  const networkDetail = networkInterfaces || "Aktywne interfejsy sieciowe";

  renderNetworkMetric(
    metricElements.rx,
    metrics.networkRxBytesPerSecond,
    metrics.networkLinkSpeedBitsPerSecond,
    `Pobieranie · ${networkDetail}`);

  renderNetworkMetric(
    metricElements.tx,
    metrics.networkTxBytesPerSecond,
    metrics.networkLinkSpeedBitsPerSecond,
    `Wysyłanie · ${networkDetail}`);

  renderWolf(metrics.wolf);

  const loadValues = [
    clampPercent(metrics.cpuPercent),
    clampPercent(gpu?.utilizationPercent),
    ramPercent,
    vramPercent
  ].filter(value => value != null);

  const wolfFull = metrics.wolf?.enabled
    && metrics.wolf?.available
    && Number(metrics.wolf.maxSessions) > 0
    && Number(metrics.wolf.activeSessions) >= Number(metrics.wolf.maxSessions);

  const highestLoad = wolfFull
    ? 100
    : (loadValues.length ? Math.max(...loadValues) : null);
  capacityBadge.className = "capacity-badge unknown";

  if (highestLoad == null) {
    capacityBadge.textContent = "Brak danych";
  } else if (highestLoad >= 85) {
    capacityBadge.className = "capacity-badge busy";
    capacityBadge.textContent = "Zajęty";
  } else if (highestLoad >= 60) {
    capacityBadge.className = "capacity-badge moderate";
    capacityBadge.textContent = "Obciążony";
  } else {
    capacityBadge.className = "capacity-badge available";
    capacityBadge.textContent = "Dostępny";
  }
}

async function refreshMetrics() {
  if (metricsRequestInProgress) return;

  if (currentState !== "online" || !currentSystemId || pendingOperation) {
    hideMetrics();
    return;
  }

  metricsRequestInProgress = true;

  try {
    const metrics = await api("/api/machine/metrics");
    renderMetrics(metrics);
  } catch {
    renderMetricsUnavailable("Nie można pobrać parametrów komputera.");
  } finally {
    metricsRequestInProgress = false;
  }
}

async function refreshStatus() {
  if (statusRequestInProgress) return;
  statusRequestInProgress = true;

  try {
    const status = await api("/api/machine/status");
    renderStatus(status);
    await refreshMetrics();
  } catch {
    renderStatus({
      online: false,
      state: "unknown",
      roundtripTimeMs: null,
      systems: configuredSystems,
      currentSystemId: null,
      currentSystemName: null,
      canRestart: false,
      canSwitchSystem: false
    });

    hideMetrics();

    if (!isBusy()) {
      setMessage("Nie można połączyć się z panelem", true);
    }
  } finally {
    statusRequestInProgress = false;
  }
}

async function runAction({
  path,
  pendingText,
  source = "power",
  operationType = null,
  targetSystemId = null
}) {
  actionInProgress = true;
  operationFailed = false;

  if (operationType) {
    beginPendingOperation(operationType, targetSystemId);
  } else if (source === "power") {
    powerButton.className = "power-button working";
  }

  updateButtons();
  setMessage(pendingText);

  try {
    const result = await api(path, { method: "POST" });

    if (!operationType) {
      setMessage(result.message ?? "Polecenie wysłane");
    }
  } catch (error) {
    clearPendingOperation();
    operationFailed = true;
    statusDot.className = "status-dot failed";
    statusText.textContent = "Błąd";
    powerButton.className = "power-button failed";
    setMessage(`Operacja nie powiodła się: ${error.message}`, true);
  } finally {
    actionInProgress = false;

    if (!pendingOperation) {
      restartButton.classList.remove("working");
      switchingSystemId = null;
    }

    updateButtons();
    window.setTimeout(refreshStatus, 1000);
  }
}

async function switchSystem(system) {
  if (isBusy()
      || currentState !== "online"
      || !canSwitchSystem
      || system.id === currentSystemId) {
    return;
  }

  if (!confirm(`Uruchomić system ${system.name}? Komputer zostanie zrestartowany.`)) {
    return;
  }

  switchingSystemId = system.id;
  document.querySelectorAll(".system-button").forEach(button => {
    button.classList.toggle(
      "switching",
      button.dataset.systemId === switchingSystemId);
  });

  await runAction({
    path: `/api/machine/systems/${encodeURIComponent(system.id)}/boot`,
    pendingText: `Przełączanie na ${system.name}…`,
    source: "system",
    operationType: "switch",
    targetSystemId: system.id
  });
}

powerButton.addEventListener("click", async () => {
  if (isBusy()) return;

  if (currentState === "offline") {
    await runAction({
      path: "/api/machine/wake",
      pendingText: "Uruchamianie komputera…",
      operationType: "wake"
    });
    return;
  }

  if (currentState === "online") {
    if (!confirm("Wyłączyć komputer? Aktywne sesje zostaną przerwane.")) {
      return;
    }

    await runAction({
      path: "/api/machine/shutdown",
      pendingText: "Wyłączanie komputera…"
    });
  }
});

restartButton.addEventListener("click", async () => {
  if (currentState !== "online" || !canRestart || isBusy()) return;
  if (!confirm("Zrestartować komputer? Aktywne sesje zostaną przerwane.")) {
    return;
  }

  await runAction({
    path: "/api/machine/restart",
    pendingText: "Restartowanie komputera…",
    source: "reset",
    operationType: "restart"
  });
});

refreshStatus();
statusTimer = window.setInterval(refreshStatus, 3000);

document.addEventListener("visibilitychange", () => {
  if (!document.hidden) refreshStatus();
});

window.addEventListener("beforeunload", () => {
  window.clearInterval(statusTimer);

  if (pendingOperationTimer !== null) {
    window.clearTimeout(pendingOperationTimer);
  }
});
