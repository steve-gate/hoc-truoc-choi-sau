FocusLock Browser Bridge V7.3

After installing/updating FocusLock:
1. Open chrome://extensions or edge://extensions
2. Enable Developer mode if needed
3. Reload "FocusLock Browser Bridge"
4. Existing http/https tabs normally receive content.js automatically in V7.3; reloading a page is still a safe fallback.

V7.3 behavior:
- sends active-tab URL/title/focus/visibility/activity/media state to FocusLock Guard;
- Guard accounts website Focus/play time directly from this heartbeat;
- blocked websites receive an in-page FocusLock overlay and remain loaded behind it;
- overlay is removed automatically after the Guard allows the page again.

V7.3 accounting:
- Sends ActiveElapsedMilliseconds for active + visible foreground tabs.
- Entertainment websites consume profile allowance / Focus wallet in Guard.
- Blocked entertainment remains on the original page with an in-tab lock overlay.
