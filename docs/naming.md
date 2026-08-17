# Product naming

The public name of the application is:

```text
SonicRelay
```

One word, capital `S` and capital `R`, no space and no underscore. This rule
covers the whole suite — the API and its docs, the .NET/Avalonia desktop
publisher, and the Flutter mobile/web viewer.

## Where the rule applies

Anywhere a person reads the product name:

- window titles, page titles, in-app headings and About screens;
- OS-level app names: Android launcher labels, iOS/macOS display names,
  Linux `.desktop` entries, Windows executable metadata;
- PWA manifests and browser tab titles;
- notifications and native dialogs;
- installers, package descriptions and release notes;
- README files, docs and screenshots;
- the Swagger UI heading (`info.title` is set to `SonicRelay API`).

When a platform has to be distinguished, extend the name rather than respell it:

```text
SonicRelay
SonicRelay Desktop
SonicRelay Mobile
SonicRelay Web
SonicRelay API
```

## Spellings that must not reach a user

```text
Sonic Relay
Sonic_Relay
sonic relay
sonic_relay
```

## Technical identifiers are exempt

Identifiers follow their ecosystem's conventions, not this rule. Lowercase and
snake_case spellings are correct where the platform expects them:

```text
sonicrelay
sonic_relay
com.vitorhugo.sonicrelay.sonic_relay
SonicRelay.Api
sonicrelay-api
sonicrelay_sessions_active
```

**Do not rename a technical identifier for aesthetic reasons.** Application IDs,
bundle identifiers, assembly names, package names, Docker image names, database
and metric names are load-bearing. Changing an application or bundle ID makes the
operating system treat the build as a different app: the user loses local state
and the store treats it as a new listing. Changing an assembly or image name
breaks installers, shortcuts and deployment pipelines.

Where a file legitimately holds both — `windows/runner/Runner.rc` in the Flutter
repo carries the display name *and* `sonic_relay.exe` — the display fields follow
this rule and the filename stays as it is.

## Enforcement

Each repository pins the rule with a test, so a regression fails CI rather than
reaching a release:

| Repository | Test |
| --- | --- |
| `dotnet_SonicRelay` | `tests/SonicRelay.Api.IntegrationTests/OpenApiBrandingTests.cs` |
| `desktop_dotnet_SonicRelay` | `tests/SonicRelay.Windows.Desktop.Tests/BrandingTests.cs` |
| `flutter_mobile-web_SonicRelay` | `test/architecture/app_branding_test.dart` |

Each of those tests asserts both halves of the rule: that the user-facing strings
say `SonicRelay`, and that the technical identifiers were *not* renamed along
with them.
