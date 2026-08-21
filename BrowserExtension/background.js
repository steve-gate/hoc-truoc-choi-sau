const NATIVE_HOST = "com.focuslock.browserbridge";
let port = null;
let reporting = false;
let heartbeatTimer = null;

function detectBrowser() {
  return navigator.userAgent.includes("Edg/") ? "edge" : "chrome";
}

function ensurePort() {
  if (port) return;
  try {
    port = chrome.runtime.connectNative(NATIVE_HOST);
    port.onMessage.addListener(onNativeMessage);
    port.onDisconnect.addListener(() => {
      port = null;
      chrome.storage.local.set({
        focusLockBridge: {
          ok: false,
          bridgeOnline: false,
          message: chrome.runtime.lastError?.message || "Native Host đã ngắt kết nối."
        }
      });
      setTimeout(ensurePort, 1000);
    });
    chrome.storage.local.set({ focusLockNativeConnected: true });
  } catch (e) {
    port = null;
    chrome.storage.local.set({
      focusLockBridge: { ok: false, bridgeOnline: false, message: String(e) }
    });
  }
}

async function collectContext() {
  try {
    const win = await chrome.windows.getLastFocused({ windowTypes: ["normal"] });
    if (!win || win.id === chrome.windows.WINDOW_ID_NONE) {
      return { browser: detectBrowser(), url: "", title: "", host: "", windowFocused: false };
    }
    const tabs = await chrome.tabs.query({ active: true, windowId: win.id });
    const tab = tabs[0];
    const url = tab?.url || "";
    let host = "";
    try {
      const parsed = new URL(url);
      if (parsed.protocol === "http:" || parsed.protocol === "https:") host = parsed.hostname.toLowerCase();
    } catch { }
    return {
      browser: detectBrowser(),
      url,
      title: tab?.title || "",
      host,
      windowFocused: !!win.focused,
      extensionVersion: chrome.runtime.getManifest().version
    };
  } catch (e) {
    return { browser: detectBrowser(), url: "", title: "", host: "", windowFocused: false, error: String(e) };
  }
}

async function reportActive() {
  if (reporting) return;
  reporting = true;
  try {
    ensurePort();
    if (!port) return;
    const context = await collectContext();
    port.postMessage({ type: "context", ...context });
  } catch {
    port = null;
  } finally {
    reporting = false;
  }
}

async function onNativeMessage(message) {
  await chrome.storage.local.set({ focusLockBridge: { ...message, receivedAt: Date.now() } });
  if (!message?.blocked || !message.url || !/^https?:\/\//i.test(message.url)) return;

  try {
    const win = await chrome.windows.getLastFocused({ windowTypes: ["normal"] });
    if (!win?.focused) return;
    const tabs = await chrome.tabs.query({ active: true, windowId: win.id });
    const tab = tabs[0];
    if (!tab?.id || tab.url !== message.url) return;

    const blockedUrl = chrome.runtime.getURL("blocked.html")
      + "?return=" + encodeURIComponent(tab.url)
      + "&host=" + encodeURIComponent(message.host || "")
      + "&reason=" + encodeURIComponent(message.message || "Trang giải trí đang bị khóa.")
      + "&balance=" + encodeURIComponent(String(message.entertainmentBalanceSeconds || 0));
    await chrome.tabs.update(tab.id, { url: blockedUrl });
  } catch { }
}

chrome.tabs.onActivated.addListener(reportActive);
chrome.tabs.onUpdated.addListener((_tabId, changeInfo) => {
  if (changeInfo.url !== undefined || changeInfo.title !== undefined || changeInfo.status === "complete") reportActive();
});
chrome.windows.onFocusChanged.addListener(reportActive);
chrome.runtime.onInstalled.addListener(() => {
  ensurePort();
  chrome.alarms.create("focuslock-reconnect", { periodInMinutes: 0.5 });
  reportActive();
});
chrome.runtime.onStartup.addListener(() => {
  ensurePort();
  chrome.alarms.create("focuslock-reconnect", { periodInMinutes: 0.5 });
  reportActive();
});
chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === "focuslock-reconnect") {
    ensurePort();
    reportActive();
  }
});
chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type === "reportNow") {
    reportActive().then(() => sendResponse({ ok: true }));
    return true;
  }
});

ensurePort();
heartbeatTimer = setInterval(reportActive, 1000);
reportActive();
