const NATIVE_HOST = "com.focuslock.browserbridge";
let port = null;
let reporting = false;
const pageStateByTab = new Map();
let lastAccountingSample = null;

function detectBrowser() {
  return navigator.userAgent.includes("Edg/") ? "edge" : "chrome";
}


async function ensureContentScript(tab) {
  try {
    if (!tab?.id || !/^https?:\/\//i.test(tab.url || "")) return;
    const state = pageStateByTab.get(tab.id);
    if (state && Date.now() - state.receivedAt <= 2500) return;
    await chrome.scripting.executeScript({
      target: { tabId: tab.id, allFrames: false },
      files: ["content.js"]
    });
  } catch { }
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
    chrome.storage.local.set({ focusLockBridge: { ok: false, bridgeOnline: false, message: String(e) } });
  }
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.type === "focuslockPageState" && sender.tab?.id) {
    pageStateByTab.set(sender.tab.id, {
      interactionCounter: Number(message.interactionCounter || 0),
      lastUserActivityUnixMs: Number(message.lastUserActivityUnixMs || 0),
      documentVisible: message.documentVisible === true,
      mediaPlaying: message.mediaPlaying === true,
      mediaProgressing: message.mediaProgressing === true,
      href: message.href || "",
      receivedAt: Date.now()
    });
    reportActive();
    sendResponse?.({ ok: true });
    return false;
  }

  if (message?.type === "reportNow") {
    reportActive().then(() => sendResponse({ ok: true }));
    return true;
  }
});

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

    let state = tab?.id ? pageStateByTab.get(tab.id) : null;
    let fresh = state && Date.now() - state.receivedAt <= 3500 && (!state.href || state.href === url);

    if (tab?.id && /^https?:\/\//i.test(url) && !fresh) {
      await ensureContentScript(tab);
      state = pageStateByTab.get(tab.id);
      fresh = state && Date.now() - state.receivedAt <= 3500 && (!state.href || state.href === url);
    }

    const now = Date.now();
    const documentVisible = fresh ? state.documentVisible === true : false;
    let activeElapsedMilliseconds = 0;
    if (lastAccountingSample &&
        lastAccountingSample.tabId === tab?.id &&
        lastAccountingSample.url === url &&
        lastAccountingSample.documentVisible &&
        documentVisible) {
      // Do not trust chrome.windows.focused for accounting. The Windows desktop
      // agent is the authoritative foreground verifier inside FocusLock Guard.
      activeElapsedMilliseconds = Math.max(0, Math.min(2500, now - lastAccountingSample.at));
    }
    lastAccountingSample = {
      tabId: tab?.id || 0,
      url,
      at: now,
      documentVisible
    };

    return {
      browser: detectBrowser(),
      url,
      title: tab?.title || "",
      host,
      windowFocused: !!win.focused,
      extensionVersion: chrome.runtime.getManifest().version,
      documentVisible,
      interactionCounter: fresh ? Number(state.interactionCounter || 0) : 0,
      lastUserActivityUnixMs: fresh ? Number(state.lastUserActivityUnixMs || 0) : 0,
      mediaPlaying: fresh ? state.mediaPlaying === true : false,
      mediaProgressing: fresh ? state.mediaProgressing === true : false,
      activeElapsedMilliseconds
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
  if (!message?.url || !/^https?:\/\//i.test(message.url)) return;

  try {
    const win = await chrome.windows.getLastFocused({ windowTypes: ["normal"] });
    if (!win || win.id === chrome.windows.WINDOW_ID_NONE) return;
    const tabs = await chrome.tabs.query({ active: true, windowId: win.id });
    const tab = tabs[0];
    if (!tab?.id || tab.url !== message.url) return;

    await ensureContentScript(tab);
    try {
      await chrome.tabs.sendMessage(tab.id, {
        type: "focuslockBlockState",
        blocked: message.blocked === true,
        reason: message.message || "Trang giải trí đang bị khóa.",
        balance: Number(message.entertainmentBalanceSeconds || 0)
      });
      return;
    } catch { }

    // Fallback only when the page refuses content-script injection.
    if (message.blocked) {
      const blockedUrl = chrome.runtime.getURL("blocked.html")
        + "?return=" + encodeURIComponent(tab.url)
        + "&host=" + encodeURIComponent(message.host || "")
        + "&reason=" + encodeURIComponent(message.message || "Trang giải trí đang bị khóa.")
        + "&balance=" + encodeURIComponent(String(message.entertainmentBalanceSeconds || 0));
      await chrome.tabs.update(tab.id, { url: blockedUrl });
    }
  } catch { }
}

chrome.tabs.onActivated.addListener(async (activeInfo) => {
  try {
    const tab = await chrome.tabs.get(activeInfo.tabId);
    await ensureContentScript(tab);
  } catch { }
  reportActive();
});
chrome.tabs.onRemoved.addListener((tabId) => pageStateByTab.delete(tabId));
chrome.tabs.onUpdated.addListener(async (tabId, changeInfo, tab) => {
  if (changeInfo.status === "complete") await ensureContentScript(tab || await chrome.tabs.get(tabId));
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

ensurePort();
setInterval(reportActive, 1000);
reportActive();
