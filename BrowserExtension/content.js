(() => {
  if (window.__focusLockV73Installed) return;
  window.__focusLockV73Installed = true;
  let interactionCounter = 0;
  let lastUserActivityUnixMs = 0;
  let lastMediaTimes = new WeakMap();
  let lastSentAt = 0;
  let lastInteractionBumpAt = 0;

  let blocked = false;
  let overlayHost = null;
  let blockReason = "Trang này đang bị FocusLock khóa.";
  let blockBalance = 0;

  function formatBalance(seconds) {
    seconds = Math.max(0, Number(seconds) || 0);
    const h = Math.floor(seconds / 3600);
    const m = Math.floor((seconds % 3600) / 60);
    const sec = seconds % 60;
    return h > 0
      ? `${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}:${String(sec).padStart(2, "0")}`
      : `${String(m).padStart(2, "0")}:${String(sec).padStart(2, "0")}`;
  }

  function ensureOverlay() {
    if (!blocked) return;
    if (overlayHost?.isConnected) {
      const reasonEl = overlayHost.shadowRoot?.getElementById("reason");
      const balanceEl = overlayHost.shadowRoot?.getElementById("balance");
      if (reasonEl) reasonEl.textContent = blockReason;
      if (balanceEl) balanceEl.textContent = `Ví Focus: ${formatBalance(blockBalance)}`;
      return;
    }

    overlayHost = document.createElement("div");
    overlayHost.id = "__focuslock_block_overlay_v73";
    overlayHost.style.cssText = "all:initial;position:fixed;inset:0;z-index:2147483647;display:block;";
    const shadow = overlayHost.attachShadow({ mode: "open" });
    shadow.innerHTML = `
      <style>
        :host{all:initial}
        .backdrop{position:fixed;inset:0;background:rgba(12,18,30,.94);display:flex;align-items:center;justify-content:center;font-family:Segoe UI,Arial,sans-serif;color:#fff}
        .card{width:min(520px,calc(100vw - 44px));background:#171d2a;border:1px solid #344054;border-radius:22px;padding:30px;box-shadow:0 24px 90px rgba(0,0,0,.5);text-align:center}
        .logo{width:54px;height:54px;border-radius:16px;background:#5b61f6;display:flex;align-items:center;justify-content:center;margin:0 auto 18px;font-size:20px;font-weight:800}
        h1{font-size:26px;line-height:1.25;margin:0 0 10px}
        p{font-size:14px;line-height:1.55;color:#cbd5e1;margin:0}
        .reason{margin-top:15px;padding:12px 14px;border-radius:12px;background:#232b3a;color:#f8fafc;font-weight:600}
        .balance{margin-top:12px;color:#93c5fd;font-size:13px;font-weight:600}
        .hint{margin-top:18px;font-size:12px;color:#94a3b8}
      </style>
      <div class="backdrop">
        <div class="card">
          <div class="logo">FL</div>
          <h1>Website đang bị khóa</h1>
          <p>FocusLock vẫn giữ nguyên trang này. Khi profile cho phép lại, màn khóa sẽ tự biến mất.</p>
          <div class="reason" id="reason"></div>
          <div class="balance" id="balance"></div>
          <div class="hint">Muốn dùng tiếp: hoàn thành Focus, dùng allowance hoặc chờ hết lịch khóa.</div>
        </div>
      </div>`;
    document.documentElement.appendChild(overlayHost);
    const reasonEl = shadow.getElementById("reason");
    const balanceEl = shadow.getElementById("balance");
    if (reasonEl) reasonEl.textContent = blockReason;
    if (balanceEl) balanceEl.textContent = `Ví Focus: ${formatBalance(blockBalance)}`;

    // Do not let audio/video continue behind the lock screen.
    for (const media of document.querySelectorAll("video,audio")) {
      try { media.pause(); } catch { }
    }
  }

  function setBlocked(next, reason, balance) {
    blocked = next === true;
    blockReason = reason || blockReason;
    blockBalance = Number(balance || 0);
    if (blocked) {
      ensureOverlay();
    } else if (overlayHost) {
      try { overlayHost.remove(); } catch { }
      overlayHost = null;
    }
  }

  chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    if (message?.type !== "focuslockBlockState") return;
    setBlocked(message.blocked === true, message.reason, message.balance);
    sendResponse?.({ ok: true });
    return false;
  });

  // A hostile/dynamic page should not be able to make itself usable by removing the overlay.
  const observer = new MutationObserver(() => {
    if (blocked && !overlayHost?.isConnected) ensureOverlay();
  });
  observer.observe(document.documentElement, { childList: true });

  for (const name of ["keydown", "keypress", "beforeinput", "wheel", "touchmove"]) {
    window.addEventListener(name, (ev) => {
      if (!blocked) return;
      ev.preventDefault();
      ev.stopImmediatePropagation();
    }, { capture: true, passive: false });
  }

  function bump(ev) {
    if (ev && ev.isTrusted === false) return;
    const now = Date.now();
    // Avoid counting a single wheel/scroll burst as hundreds of events.
    if (now - lastInteractionBumpAt < 250) return;
    lastInteractionBumpAt = now;
    interactionCounter++;
    lastUserActivityUnixMs = now;
  }

  for (const name of ["pointerdown", "keydown", "wheel", "touchstart"]) {
    window.addEventListener(name, bump, { capture: true, passive: true });
  }
  window.addEventListener("scroll", bump, { capture: true, passive: true });

  function readMediaState() {
    let mediaPlaying = false;
    let mediaProgressing = false;
    const media = document.querySelectorAll("video, audio");
    for (const el of media) {
      try {
        const playing = !el.paused && !el.ended && el.readyState >= 2 && el.playbackRate > 0;
        if (!playing) {
          lastMediaTimes.set(el, Number(el.currentTime) || 0);
          continue;
        }
        mediaPlaying = true;
        const current = Number(el.currentTime) || 0;
        const previous = lastMediaTimes.get(el);
        if (typeof previous === "number" && current > previous + 0.05) mediaProgressing = true;
        lastMediaTimes.set(el, current);
      } catch { }
    }
    return { mediaPlaying, mediaProgressing };
  }

  function sendState() {
    const now = Date.now();
    if (now - lastSentAt < 700) return;
    lastSentAt = now;
    const media = readMediaState();
    try {
      chrome.runtime.sendMessage({
        type: "focuslockPageState",
        interactionCounter,
        lastUserActivityUnixMs,
        documentVisible: document.visibilityState === "visible",
        mediaPlaying: media.mediaPlaying,
        mediaProgressing: media.mediaProgressing,
        href: location.href
      });
    } catch { }
  }

  document.addEventListener("visibilitychange", sendState, true);
  document.addEventListener("play", sendState, true);
  document.addEventListener("pause", sendState, true);
  document.addEventListener("seeking", sendState, true);
  window.addEventListener("focus", sendState, true);
  window.addEventListener("pageshow", sendState, true);
  setInterval(sendState, 1000);
  sendState();
})();
