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
let failedTargetSystemId = null;
let graphicalInterfaceStatus = null;
let graphicalActionInProgress = false;

const OPERATION_TIMEOUT_MS = 30_000;
const BOOT_SEQUENCE_TIMEOUT_MS = 270_000;
const RESTART_MINIMUM_WAIT_MS = 8_000;

const actionMessage = document.querySelector("#actionMessage");
const powerButton = document.querySelector("#powerButton");
const restartButton = document.querySelector("#restartButton");
const resetControls = document.querySelector(".reset-controls");
const graphicsControls = document.querySelector("#graphicsControls");
const graphicsButton = document.querySelector("#graphicsButton");
const graphicsState = document.querySelector("#graphicsState");
const graphicsMessage = document.querySelector("#graphicsMessage");
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
  return actionInProgress
    || graphicalActionInProgress
    || pendingOperation !== null;
}

function operationLabel(type) {
  if (type === "wake") return "Uruchamianie…";
  if (type === "switch") return "Przełączanie systemu…";
  if (type === "cold-switch") return "Uruchamianie wybranego systemu…";
  return "Restartowanie…";
}

function operationTimeout(type) {
  return type === "cold-switch"
    ? BOOT_SEQUENCE_TIMEOUT_MS
    : OPERATION_TIMEOUT_MS;
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
    || pendingOperation.type === "switch"
    || pendingOperation.type === "cold-switch";
  restartButton.classList.toggle("working", restartWorking);
}

function beginPendingOperation(type, targetSystemId = null) {
  if (pendingOperationTimer !== null) {
    window.clearTimeout(pendingOperationTimer);
  }

  operationFailed = false;
  failedTargetSystemId = null;
  const startedAt = Date.now();
  const timeout = operationTimeout(type);

  pendingOperation = {
    type,
    targetSystemId,
    startedAt,
    deadline: startedAt + timeout,
    sawOffline: false
  };

  pendingOperationTimer = window.setTimeout(() => {
    if (pendingOperation) {
      failPendingOperation();
    }
  }, timeout + 100);

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

function failPendingOperation(message = "Nie udało się wykryć docelowego systemu w wymaganym czasie.") {
  failedTargetSystemId = pendingOperation?.targetSystemId ?? null;
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

  if (pendingOperation.type === "switch"
      || pendingOperation.type === "cold-switch") {
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
    failedTargetSystemId = null;
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
  const offline = currentState === "offline";
  const restartAvailable = online && canRestart;
  const systemStartAvailable = offline
    || (online && (canSwitchSystem || canRestart));
  const busy = isBusy();

  powerButton.disabled = busy || !knownState;

  restartButton.disabled = busy || !restartAvailable;
  resetControls.classList.toggle("locked", !restartAvailable && !pendingOperation);
  resetControls.setAttribute(
    "aria-disabled",
    String(!restartAvailable || busy));

  const graphicsAvailable = Boolean(
    graphicalInterfaceStatus?.enabled
      && graphicalInterfaceStatus?.available);

  graphicsButton.disabled = busy || !graphicsAvailable;
  graphicsControls.classList.toggle(
    "locked",
    !graphicsAvailable && !graphicalActionInProgress);
  graphicsControls.setAttribute(
    "aria-disabled",
    String(!graphicsAvailable || busy));

  systemsSection.classList.toggle(
    "locked",
    !systemStartAvailable && !pendingOperation);
  systemsSection.setAttribute(
    "aria-disabled",
    String(!systemStartAvailable || busy));

  document.querySelectorAll(".system-button").forEach(button => {
    const isCurrent = online
      && button.dataset.systemId === currentSystemId;
    button.disabled = busy
      || !knownState
      || !systemStartAvailable
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

  if (currentState === "offline") {
    systemHint.textContent = "";
    systemHint.hidden = true;
  } else if (currentState === "online" && !canSwitchSystem && canRestart) {
    systemHint.textContent = "Zmiana systemu wykona restart do domyślnego Linuksa, a następnie ustawi wybrany wpis GRUB.";
    systemHint.hidden = false;
  } else {
    systemHint.textContent = "";
    systemHint.hidden = true;
  }

  updateButtons();
}

function renderGraphicalInterface(status) {
  graphicalInterfaceStatus = status ?? null;

  const configured = Boolean(status?.enabled);
  graphicsControls.hidden = !configured;

  if (!configured) {
    graphicsButton.className = "graphics-button";
    graphicsButton.setAttribute("aria-pressed", "false");
    graphicsState.textContent = "Wyłączone";
    graphicsMessage.textContent = "";
    updateButtons();
    return;
  }

  const active = Boolean(status.active);
  graphicsButton.className = graphicalActionInProgress
    ? "graphics-button working"
    : `graphics-button ${active ? "active" : "inactive"}`;
  graphicsButton.setAttribute("aria-pressed", String(active));
  graphicsButton.setAttribute(
    "aria-label",
    active
      ? "Wyłącz interfejs graficzny"
      : "Uruchom interfejs graficzny");
  graphicsButton.setAttribute(
    "title",
    active
      ? "Wyłącz GUI i pozostaw konsolę"
      : "Uruchom GUI");

  if (graphicalActionInProgress) {
    graphicsState.textContent = "Przełączanie…";
    graphicsMessage.textContent = "Wysyłanie polecenia do domyślnego Linuksa.";
  } else if (!status.available) {
    graphicsState.textContent = "Niedostępne";
    graphicsMessage.textContent = status.message ?? "Uruchom domyślny Linux.";
  } else {
    graphicsState.textContent = active ? "GUI włączone" : "Tylko konsola";
    graphicsMessage.textContent = status.message ?? "";
  }

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
  if (operationFailed
      && status.online
      && status.currentSystemId
      && (!failedTargetSystemId
          || status.currentSystemId === failedTargetSystemId)) {
    operationFailed = false;
    failedTargetSystemId = null;
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

function syncBootSequence(bootSequence) {
  if (!bootSequence) return;

  const matchingColdSwitch = pendingOperation?.type === "cold-switch"
    && pendingOperation.targetSystemId === bootSequence.targetSystemId;

  if (bootSequence.active) {
    switchingSystemId = bootSequence.targetSystemId;

    if (!matchingColdSwitch) {
      beginPendingOperation("cold-switch", bootSequence.targetSystemId);
    }

    setMessage(bootSequence.message || "Uruchamianie wybranego systemu…");
    return;
  }

  if (matchingColdSwitch
      && bootSequence.stage === "failed") {
    failPendingOperation(bootSequence.message);
  }
}

async function refreshStatus() {
  if (statusRequestInProgress) return;
  statusRequestInProgress = true;

  try {
    const [status, bootSequence, graphicalInterface] = await Promise.all([
      api("/api/machine/status"),
      api("/api/machine/boot-sequence"),
      api("/api/machine/graphical-interface")
    ]);

    syncBootSequence(bootSequence);
    renderStatus(status);
    renderGraphicalInterface(graphicalInterface);
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

    renderGraphicalInterface(graphicalInterfaceStatus);
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
    failedTargetSystemId = targetSystemId;
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

async function toggleGraphicalInterface() {
  if (isBusy() || !graphicalInterfaceStatus?.available) return;

  if (graphicalInterfaceStatus.active
      && !confirm("Wyłączyć interfejs graficzny? Lokalna sesja pulpitu i otwarte programy graficzne zostaną zamknięte.")) {
    return;
  }

  graphicalActionInProgress = true;
  renderGraphicalInterface(graphicalInterfaceStatus);
  setMessage(
    graphicalInterfaceStatus.active
      ? "Wyłączanie interfejsu graficznego…"
      : "Uruchamianie interfejsu graficznego…");

  try {
    const result = await api(
      "/api/machine/graphical-interface/toggle",
      { method: "POST" });

    setMessage(result.message ?? "Przełączono interfejs graficzny.");
  } catch (error) {
    setMessage(`Nie udało się przełączyć GUI: ${error.message}`, true);
  } finally {
    graphicalActionInProgress = false;
    updateButtons();
    window.setTimeout(refreshStatus, 500);
  }
}

async function switchSystem(system) {
  const online = currentState === "online";
  const offline = currentState === "offline";
  const canStartSystem = offline
    || (online && (canSwitchSystem || canRestart));

  if (isBusy()
      || !canStartSystem
      || (online && system.id === currentSystemId)) {
    return;
  }

  const needsBootSequence = offline || !canSwitchSystem;
  const question = needsBootSequence
    ? `Uruchomić system ${system.name}? Najpierw wystartuje domyślny Linux, który ustawi GRUB.`
    : `Uruchomić system ${system.name}? Komputer zostanie zrestartowany.`;

  if (!confirm(question)) {
    return;
  }

  switchingSystemId = system.id;
  document.querySelectorAll(".system-button").forEach(button => {
    button.classList.toggle(
      "switching",
      button.dataset.systemId === switchingSystemId);
  });

  await runAction({
    path: needsBootSequence
      ? `/api/machine/systems/${encodeURIComponent(system.id)}/wake-boot`
      : `/api/machine/systems/${encodeURIComponent(system.id)}/boot`,
    pendingText: needsBootSequence
      ? `Uruchamianie ${system.name} przez domyślnego Linuksa…`
      : `Przełączanie na ${system.name}…`,
    source: "system",
    operationType: needsBootSequence ? "cold-switch" : "switch",
    targetSystemId: system.id
  });
}

graphicsButton.addEventListener("click", toggleGraphicalInterface);

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

// -----------------------------------------------------------------------------
// NAS - niezależne urządzenie
// -----------------------------------------------------------------------------
let nasState = "unknown";
let nasEnabled = false;
let nasActionInProgress = false;
let nasRequestInProgress = false;
let nasPendingOperation = null;
let nasPendingTimer = null;
let nasRefreshTimer = null;

const NAS_WAKE_TIMEOUT_MS = 180_000;
const NAS_SHUTDOWN_TIMEOUT_MS = 90_000;
const NAS_RESTART_TIMEOUT_MS = 180_000;

const nasDevice = document.querySelector("#nasDevice");
const nasTitle = document.querySelector("#nasTitle");
const nasStatusDot = document.querySelector("#nasStatusDot");
const nasStatusText = document.querySelector("#nasStatusText");
const nasPowerButton = document.querySelector("#nasPowerButton");
const nasRestartButton = document.querySelector("#nasRestartButton");
const nasResetControls = document.querySelector("#nasResetControls");
const nasActionMessage = document.querySelector("#nasActionMessage");
const nasMetricsSection = document.querySelector("#nasMetricsSection");
const nasHealthBadge = document.querySelector("#nasHealthBadge");
const nasMetricsMessage = document.querySelector("#nasMetricsMessage");
const nasStorageState = document.querySelector("#nasStorageState");
const nasStoragePool = document.querySelector("#nasStoragePool");
const nasUptime = document.querySelector("#nasUptime");
const nasHostInfo = document.querySelector("#nasHostInfo");
const nasConnections = document.querySelector("#nasConnections");
const nasSystemTemperature = document.querySelector("#nasSystemTemperature");
const nasDiskCount = document.querySelector("#nasDiskCount");
const nasDiskList = document.querySelector("#nasDiskList");
const nasDiskMessage = document.querySelector("#nasDiskMessage");

const nasMetricElements = {
  storage: metricElement("nasStorage"),
  cpu: metricElement("nasCpu"),
  ram: metricElement("nasRam"),
  read: metricElement("nasRead"),
  write: metricElement("nasWrite"),
  rx: metricElement("nasRx"),
  tx: metricElement("nasTx")
};

function setNasMessage(text, isError = false) {
  nasActionMessage.textContent = text || "";
  nasActionMessage.classList.toggle("error", isError);
}

function clearNasPendingOperation() {
  if (nasPendingTimer !== null) {
    window.clearTimeout(nasPendingTimer);
    nasPendingTimer = null;
  }

  nasPendingOperation = null;
}

function beginNasPendingOperation(type) {
  clearNasPendingOperation();

  const timeout = type === "shutdown"
    ? NAS_SHUTDOWN_TIMEOUT_MS
    : (type === "restart" ? NAS_RESTART_TIMEOUT_MS : NAS_WAKE_TIMEOUT_MS);

  nasPendingOperation = {
    type,
    deadline: Date.now() + timeout,
    sawOffline: false
  };

  nasPendingTimer = window.setTimeout(() => {
    if (!nasPendingOperation) return;
    clearNasPendingOperation();
    setNasMessage("Nie udało się potwierdzić zakończenia operacji NAS w wymaganym czasie.", true);
    updateNasButtons();
  }, timeout + 100);
}

function nasPendingFinished(status) {
  if (!nasPendingOperation) return false;

  if (status.state === "offline") {
    nasPendingOperation.sawOffline = true;
  }

  if (nasPendingOperation.type === "wake") {
    return Boolean(status.online);
  }

  if (nasPendingOperation.type === "shutdown") {
    return status.state === "offline";
  }

  return nasPendingOperation.sawOffline && Boolean(status.online);
}

function updateNasButtons() {
  if (!nasEnabled) return;

  const knownState = nasState === "online" || nasState === "offline";
  const busy = nasActionInProgress || nasPendingOperation !== null;

  nasPowerButton.disabled = busy || !knownState;
  nasRestartButton.disabled = busy || nasState !== "online";
  nasResetControls.classList.toggle("locked", nasState !== "online" && !busy);
}

function renderNasWorkingStatus() {
  const type = nasPendingOperation?.type;
  nasStatusDot.className = "status-dot working";
  nasPowerButton.className = "power-button working";

  if (type === "shutdown") {
    nasStatusText.textContent = "Wyłączanie…";
  } else if (type === "restart") {
    nasStatusText.textContent = "Restartowanie…";
    nasRestartButton.classList.add("working");
  } else {
    nasStatusText.textContent = "Uruchamianie…";
  }

  updateNasButtons();
}

function renderNasStatus(status) {
  nasEnabled = Boolean(status?.enabled);
  nasDevice.hidden = !nasEnabled;

  if (!nasEnabled) return;

  nasTitle.textContent = status.name || "NAS";
  nasState = status.state || "unknown";

  if (nasPendingOperation) {
    if (nasPendingFinished(status)) {
      clearNasPendingOperation();
      nasRestartButton.classList.remove("working");
      setNasMessage("");
    } else if (Date.now() < nasPendingOperation.deadline) {
      renderNasWorkingStatus();
      return;
    }
  }

  nasRestartButton.classList.remove("working");
  nasStatusDot.className = `status-dot ${nasState}`;
  nasPowerButton.className = `power-button ${nasState}`;

  if (status.online) {
    nasStatusText.textContent = status.roundtripTimeMs == null
      ? "Online"
      : `Online · ${status.roundtripTimeMs} ms`;
    nasPowerButton.setAttribute("aria-label", "Wyłącz NAS");
    nasPowerButton.setAttribute("title", "Wyłącz NAS");
  } else if (nasState === "offline") {
    nasStatusText.textContent = "Offline";
    nasPowerButton.setAttribute("aria-label", "Włącz NAS");
    nasPowerButton.setAttribute("title", "Włącz NAS");
  } else {
    nasStatusText.textContent = "Brak połączenia";
    nasPowerButton.setAttribute("aria-label", "Stan NAS jest nieznany");
    nasPowerButton.removeAttribute("title");
  }

  updateNasButtons();
}

function formatStorageBytes(value) {
  if (!Number.isFinite(value) || value < 0) return "—";

  const tib = value / (1024 ** 4);
  if (tib >= 0.9) {
    return `${tib >= 10 ? tib.toFixed(1) : tib.toFixed(2)} TB`;
  }

  const gib = value / (1024 ** 3);
  if (gib >= 1) {
    return `${gib >= 10 ? gib.toFixed(0) : gib.toFixed(1)} GB`;
  }

  const mib = value / (1024 ** 2);
  return `${mib.toFixed(0)} MB`;
}

function formatDiskRate(bytesPerSecond) {
  if (!Number.isFinite(bytesPerSecond) || bytesPerSecond < 0) return "—";

  if (bytesPerSecond >= 1024 ** 3) {
    return `${(bytesPerSecond / (1024 ** 3)).toFixed(2)} GB/s`;
  }
  if (bytesPerSecond >= 1024 ** 2) {
    return `${(bytesPerSecond / (1024 ** 2)).toFixed(1)} MB/s`;
  }
  if (bytesPerSecond >= 1024) {
    return `${(bytesPerSecond / 1024).toFixed(0)} KB/s`;
  }
  return `${Math.round(bytesPerSecond)} B/s`;
}

function formatUptime(seconds) {
  if (!Number.isFinite(seconds) || seconds < 0) return "—";

  const totalHours = Math.floor(seconds / 3600);
  const days = Math.floor(totalHours / 24);
  const hours = totalHours % 24;
  const minutes = Math.floor((seconds % 3600) / 60);

  if (days > 0) return `${days} d ${hours} h`;
  if (totalHours > 0) return `${totalHours} h ${minutes} min`;
  return `${minutes} min`;
}

function renderNasRateMetric(element, bytesPerSecond, detail) {
  const valid = Number.isFinite(bytesPerSecond) && bytesPerSecond >= 0;
  element.value.textContent = valid ? formatDiskRate(bytesPerSecond) : "—";
  element.bar.style.width = "0%";
  element.detail.textContent = detail || "—";
  element.card.classList.toggle("unavailable", !valid);
  element.card.classList.remove("moderate", "busy");
}

function normalizeHealth(value) {
  return String(value || "").trim().toLowerCase();
}

function healthLevel(value) {
  const state = normalizeHealth(value);
  if (!state) return "unknown";
  if (["healthy", "ok", "passed", "online", "optimal", "good"].includes(state)) {
    return "available";
  }
  if (["warning", "rebuilding", "resilvering", "scrubbing"].includes(state)) {
    return "moderate";
  }
  if (["degraded", "failed", "faulted", "critical", "bad", "error"].includes(state)) {
    return "busy";
  }
  return "unknown";
}

function renderNasHealth(metrics) {
  const storageLevel = healthLevel(metrics.storageState);
  const disks = Array.isArray(metrics.disks) ? metrics.disks : [];
  const diskLevels = disks.map(disk => healthLevel(disk.smartStatus));

  let level = storageLevel;
  if (diskLevels.includes("busy")) level = "busy";
  else if (level !== "busy" && diskLevels.includes("moderate")) level = "moderate";
  else if (level === "unknown" && diskLevels.length > 0 && diskLevels.every(x => x === "available")) level = "available";

  nasHealthBadge.className = `capacity-badge ${level}`;
  nasHealthBadge.textContent = level === "available"
    ? "Healthy"
    : (level === "moderate" ? "Warning" : (level === "busy" ? "Degraded" : "Brak danych"));
}

function renderNasDisks(disks) {
  const items = Array.isArray(disks) ? disks : [];
  nasDiskList.replaceChildren();
  nasDiskCount.textContent = String(items.length);
  nasDiskMessage.textContent = items.length === 0 ? "Brak danych o dyskach." : "";

  for (const disk of items) {
    const item = document.createElement("article");
    item.className = "nas-disk-item";

    const header = document.createElement("div");
    header.className = "nas-disk-header";

    const identity = document.createElement("div");
    identity.className = "nas-disk-identity";
    const name = document.createElement("strong");
    name.textContent = disk.name || disk.id || "Dysk";
    const model = document.createElement("span");
    model.textContent = disk.model || formatStorageBytes(disk.capacityBytes);
    identity.append(name, model);

    const smart = document.createElement("span");
    const smartLevel = healthLevel(disk.smartStatus);
    smart.className = `nas-smart-badge ${smartLevel}`;
    smart.textContent = disk.smartStatus || "Unknown";

    header.append(identity, smart);

    const details = document.createElement("div");
    details.className = "nas-disk-details";

    const capacity = document.createElement("span");
    capacity.innerHTML = `<small>POJEMNOŚĆ</small><strong>${formatStorageBytes(disk.capacityBytes)}</strong>`;

    const temperature = document.createElement("span");
    const tempValue = Number.isFinite(disk.temperatureCelsius)
      ? `${Math.round(disk.temperatureCelsius)}°C`
      : "—";
    temperature.innerHTML = `<small>TEMPERATURA</small><strong>${tempValue}</strong>`;
    if (Number.isFinite(disk.temperatureCelsius) && disk.temperatureCelsius >= 55) {
      temperature.classList.add("hot");
    } else if (Number.isFinite(disk.temperatureCelsius) && disk.temperatureCelsius >= 45) {
      temperature.classList.add("warm");
    }

    details.append(capacity, temperature);
    item.append(header, details);
    nasDiskList.append(item);
  }
}

function renderNasMetricsUnavailable(message) {
  nasMetricsSection.classList.add("unavailable");
  nasHealthBadge.className = "capacity-badge unknown";
  nasHealthBadge.textContent = "Brak danych";
  nasMetricsMessage.textContent = message || "Agent NAS nie odpowiada.";

  renderMetric(nasMetricElements.storage, null, "—");
  renderMetric(nasMetricElements.cpu, null, "—");
  renderMetric(nasMetricElements.ram, null, "—");
  renderNasRateMetric(nasMetricElements.read, null, "Dyski / pool");
  renderNasRateMetric(nasMetricElements.write, null, "Dyski / pool");
  renderNetworkMetric(nasMetricElements.rx, null, null, "Download");
  renderNetworkMetric(nasMetricElements.tx, null, null, "Upload");

  nasStorageState.textContent = "—";
  nasStorageState.className = "";
  nasStoragePool.textContent = "—";
  nasUptime.textContent = "—";
  nasHostInfo.textContent = "—";
  nasConnections.textContent = "—";
  nasSystemTemperature.textContent = "—";
  renderNasDisks([]);
}

function renderNasMetrics(metrics) {
  if (!metrics?.available) {
    renderNasMetricsUnavailable(metrics?.message);
    return;
  }

  nasMetricsSection.classList.remove("unavailable");
  nasMetricsMessage.textContent = "";

  const storagePercent = ratioPercent(
    metrics.storageUsedBytes,
    metrics.storageTotalBytes);
  const ramPercent = ratioPercent(
    metrics.memoryUsedBytes,
    metrics.memoryTotalBytes);

  renderMetric(
    nasMetricElements.storage,
    storagePercent,
    storagePercent == null
      ? "—"
      : `${Math.round(storagePercent)}% zajęte${metrics.storagePoolName ? ` · ${metrics.storagePoolName}` : ""}`);
  nasMetricElements.storage.value.textContent =
    Number.isFinite(metrics.storageUsedBytes) && Number.isFinite(metrics.storageTotalBytes)
      ? `${formatStorageBytes(metrics.storageUsedBytes)} / ${formatStorageBytes(metrics.storageTotalBytes)}`
      : "—";

  const cpuTemps = [];
  if (Number.isFinite(metrics.cpuTemperatureCelsius)) {
    cpuTemps.push(`CPU ${Math.round(metrics.cpuTemperatureCelsius)}°C`);
  }
  if (Number.isFinite(metrics.systemTemperatureCelsius)) {
    cpuTemps.push(`System ${Math.round(metrics.systemTemperatureCelsius)}°C`);
  }

  renderMetric(
    nasMetricElements.cpu,
    metrics.cpuPercent,
    cpuTemps.length ? cpuTemps.join(" · ") : "Obciążenie procesora");

  renderMetric(
    nasMetricElements.ram,
    ramPercent,
    `${formatBytes(metrics.memoryUsedBytes)} / ${formatBytes(metrics.memoryTotalBytes)}`);

  renderNasRateMetric(
    nasMetricElements.read,
    metrics.diskReadBytesPerSecond,
    "Aktualny odczyt storage");
  renderNasRateMetric(
    nasMetricElements.write,
    metrics.diskWriteBytesPerSecond,
    "Aktualny zapis storage");

  const interfaces = Array.isArray(metrics.networkInterfaces)
    ? metrics.networkInterfaces.filter(Boolean).join(", ")
    : "";
  const networkDetail = interfaces || "Interfejs LAN";

  renderNetworkMetric(
    nasMetricElements.rx,
    metrics.networkRxBytesPerSecond,
    metrics.networkLinkSpeedBitsPerSecond,
    `Download · ${networkDetail}`);
  renderNetworkMetric(
    nasMetricElements.tx,
    metrics.networkTxBytesPerSecond,
    metrics.networkLinkSpeedBitsPerSecond,
    `Upload · ${networkDetail}`);

  nasStorageState.textContent = metrics.storageState || "Unknown";
  nasStorageState.className = `health-text ${healthLevel(metrics.storageState)}`;
  nasStoragePool.textContent = metrics.storagePoolName || "Storage pool";
  nasUptime.textContent = formatUptime(metrics.uptimeSeconds);

  const hostParts = [];
  if (metrics.hostName) hostParts.push(metrics.hostName);
  if (metrics.operatingSystem) hostParts.push(metrics.operatingSystem);
  nasHostInfo.textContent = hostParts.length ? hostParts.join(" · ") : "NAS";

  nasConnections.textContent = Number.isFinite(metrics.activeConnections)
    ? String(metrics.activeConnections)
    : "—";

  const systemTemps = [];
  if (Number.isFinite(metrics.cpuTemperatureCelsius)) {
    systemTemps.push(`${Math.round(metrics.cpuTemperatureCelsius)}°C CPU`);
  }
  if (Number.isFinite(metrics.systemTemperatureCelsius)) {
    systemTemps.push(`${Math.round(metrics.systemTemperatureCelsius)}°C SYS`);
  }
  nasSystemTemperature.textContent = systemTemps.length
    ? systemTemps.join(" / ")
    : "—";

  renderNasDisks(metrics.disks);
  renderNasHealth(metrics);
}

async function refreshNasMetrics() {
  if (!nasEnabled || nasState !== "online") {
    renderNasMetricsUnavailable(
      nasState === "offline"
        ? "NAS jest wyłączony."
        : "Stan NAS jest nieznany lub konfiguracja nie jest jeszcze kompletna.");
    return;
  }

  try {
    const metrics = await api("/api/nas/metrics");
    renderNasMetrics(metrics);
  } catch {
    renderNasMetricsUnavailable("Nie można pobrać parametrów NAS.");
  }
}

async function refreshNasStatus() {
  if (nasRequestInProgress) return;
  nasRequestInProgress = true;

  try {
    const status = await api("/api/nas/status");
    renderNasStatus(status);

    if (status.enabled) {
      await refreshNasMetrics();
    }
  } catch {
    if (nasEnabled) {
      renderNasStatus({
        enabled: true,
        name: nasTitle.textContent || "NAS",
        online: false,
        state: "unknown",
        roundtripTimeMs: null
      });
      renderNasMetricsUnavailable("Nie można połączyć się z obsługą NAS w panelu.");
    }
  } finally {
    nasRequestInProgress = false;
  }
}

async function runNasAction(path, pendingText, type) {
  if (nasActionInProgress || nasPendingOperation) return;

  nasActionInProgress = true;
  beginNasPendingOperation(type);
  renderNasWorkingStatus();
  setNasMessage(pendingText);

  try {
    const result = await api(path, { method: "POST" });
    setNasMessage(result.message || pendingText);
  } catch (error) {
    clearNasPendingOperation();
    nasStatusDot.className = "status-dot failed";
    nasStatusText.textContent = "Błąd";
    nasPowerButton.className = "power-button failed";
    setNasMessage(`Operacja NAS nie powiodła się: ${error.message}`, true);
  } finally {
    nasActionInProgress = false;
    updateNasButtons();
    window.setTimeout(refreshNasStatus, 800);
  }
}

nasPowerButton.addEventListener("click", async () => {
  if (!nasEnabled || nasActionInProgress || nasPendingOperation) return;

  if (nasState === "offline") {
    await runNasAction(
      "/api/nas/wake",
      "Uruchamianie NAS przez Wake-on-LAN…",
      "wake");
    return;
  }

  if (nasState === "online") {
    if (!confirm("Wyłączyć NAS? Upewnij się, że nie trwa zapis danych ani przebudowa RAID.")) {
      return;
    }

    await runNasAction(
      "/api/nas/shutdown",
      "Bezpieczne wyłączanie NAS…",
      "shutdown");
  }
});

nasRestartButton.addEventListener("click", async () => {
  if (!nasEnabled || nasState !== "online" || nasActionInProgress || nasPendingOperation) return;

  if (!confirm("Zrestartować NAS? Aktywne połączenia zostaną przerwane.")) {
    return;
  }

  await runNasAction(
    "/api/nas/restart",
    "Restartowanie NAS…",
    "restart");
});

refreshNasStatus();
nasRefreshTimer = window.setInterval(refreshNasStatus, 3000);

document.addEventListener("visibilitychange", () => {
  if (!document.hidden) refreshNasStatus();
});

window.addEventListener("beforeunload", () => {
  window.clearInterval(nasRefreshTimer);
  if (nasPendingTimer !== null) {
    window.clearTimeout(nasPendingTimer);
  }
});
