FocusLock V7.8.0.2 Persistence V2.2.2 CODE-FOLDER-ONLY

Purpose:
- Keep FocusLock data only inside the code folder: FocusLock-Data\Data
- Do not replace App or Service binaries.
- Pin only the FocusLockGuard service environment to the D-drive code folder.
- Remove any old machine-wide FOCUSLOCK_HOME override.
- Prove persistence by asking the existing Guard to create a temporary backup through its Named Pipe.
  The Guard backup operation calls Save(state), so state.v2.json must change and remain HMAC-valid.
- Delete the temporary proof backup after verification.

Run as Administrator:
  APPLY_V7_8_0_2_PERSISTENCE_V2_2_2_CODE_FOLDER_ONLY.bat

Then check:
  PERSISTENCE_STATUS_V2_2_2_CODE_FOLDER_ONLY.bat

No FocusLock state or secret is written to C: by this package.
