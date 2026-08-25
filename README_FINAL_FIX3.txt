FocusLock V7.8.0.2 FINAL FIX 3

Root cause addressed:
- Legacy state envelopes can begin with UTF-8 BOM EF BB BF because Save used Encoding.UTF8.
- System.Text.Json raw-byte parsing does not accept that BOM unless it is removed first.
- PowerShell ReadAllText accepted the same file, which is why installer HMAC proof could pass while Guard Load failed.

FIX3:
1. Preserves current FocusLock-Data if HMAC-valid.
2. Removes only the outer UTF-8 BOM from state.v2.json/state.v2.bak (payload and HMAC are unchanged).
3. SecureStateStore strips legacy BOM while reading.
4. New Save writes UTF-8 without BOM.
5. Keeps fail-closed secret/state recovery from FIX2.
6. Keeps no-sample-rule-regeneration fix.
7. Keeps hidden-window activation fix.
8. Data stays at <CodeRoot>\FocusLock-Data\Data on drive D.
