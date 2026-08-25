FocusLock V7.8.0.2 FINAL FIX 2

Root cause confirmed by Windows Event Log:
SecureStateStore.Load() failed because signed state existed in FocusLock-Data but the active readable secret did not verify it.
The previous fail-safe still had a flaw: LoadOrCreateSecret trusted any readable guard.secret and could overwrite guard.secret.bak BEFORE proving which secret matched the signed state.

This fix:
1. Keeps persistence only at <CodeRoot>\FocusLock-Data\Data.
2. Tries BOTH guard.secret and guard.secret.bak against state.v2.json BEFORE falling back to state.v2.bak.
3. Never overwrites one secret with another until HMAC verification proves which key is correct.
4. Never creates a new secret while signed state exists.
5. Repairs one coherent HMAC-valid state+secret pair before starting Guard.
6. Fixes sample websites reappearing after intentional deletion.
7. Fixes reopening the hidden tray window by launching FocusLock.exe again.
8. Stops the current Guard crash loop before repair.
9. Builds into a new FINAL-FIX2 OneDir and never overwrites locked old runtime DLLs.

Run as Administrator:
APPLY_FINAL_FIX2.bat

Then verify before and after reboot:
VERIFY_FINAL_FIX2.bat
