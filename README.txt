FocusLock V7.8.0.2 Persistence V2.1.1 CONFIG-ONLY

Purpose:
- Fix ONLY the PowerShell 5.1 parser/encoding problem in V2.1.
- No Service EXE/DLL is built, moved, deleted, or overwritten.
- No App/UI/Bootstrap file is changed.
- The persistence logic is unchanged from V2.1.

Run as Administrator:
  APPLY_V7_8_0_2_PERSISTENCE_V2_1_1.bat

Then verify:
  PERSISTENCE_STATUS_V2_1_1.bat

All PowerShell scripts in this package are strict 7-bit ASCII.
