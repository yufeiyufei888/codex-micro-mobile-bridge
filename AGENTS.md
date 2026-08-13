# Codex Micro contributor notes

Android, Bridge, protocol, and end-to-end verification rules are documented in
[`docs/testing.md`](docs/testing.md). Read that file before modifying tests or
claiming a release has passed.

- Keep fast Android business tests in `android/app/src/test`.
- Keep Room SQLite and Compose device tests in `android/app/src/androidTest`.
- Compile-only verification is not a physical-device test result.
- Do not add a dependency-injection or screenshot framework without isolating
  and validating the migration first.
- Do not publish an APK unless application ID, versionCode, and signing
  certificate match the release policy.
