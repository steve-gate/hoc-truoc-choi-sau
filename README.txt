FocusLock V7.7.0 - Profile-first Quick Add

UX redesign:
- Select a Profile, then add entertainment apps directly there:
  + Running app
  + Choose .exe
- Add websites directly there:
  + Enter domain
  + Use current website
- If an app/domain already exists in another Profile, it moves directly to the selected Profile.
- Policy editor is policy-only; the old giant membership checkbox panel is hidden.
- Global App/Web pages remain inventories, not mandatory steps.

Service change:
- AddApp/AddBrowserRule now honor a valid requested BlockProfileId instead of always forcing the default profile.

Safety:
- publish\Data untouched.
- Browser Core and NativeHost untouched.
- Installer also cleans the known Run/backtick compile artifacts before building.
