FocusLock FIX3 post-reboot diagnostic

Purpose:
- Read-only diagnosis after reboot when Guard pipe is offline and VERIFY_FINAL_FIX3 reports STATE: INVALID.
- Tests state.v2.json and state.v2.bak against guard.secret and guard.secret.bak separately.
- Captures exact hashes, timestamps, ACLs, service path, startup tasks and recent .NET/SCM errors.

Run from the FocusLock code root as Administrator:
  DIAGNOSE_FIX3_AFTER_REBOOT.bat

The script does NOT modify FocusLock Data and does NOT reconfigure or restart the service.
