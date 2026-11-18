# Signing renewal: scripted certificate rotation

**Status: planned, not started.**

## Why

The macOS release signs with `MACOS_CERT_P12` / `MACOS_CERT_PASSWORD` - the Developer ID
Application certificate and its private key. Certificates last 5 years. The app claims no
restricted entitlement, so there is no provisioning profile to reissue alongside it. A Mac App
Store build would need a profile, but a Mac App Store one under its own distribution certificate
(see `store-distribution-comparison.md`), so this script would gain that leg alongside the store
build rather than keep the Developer ID profile in case.

Doing that by hand means Keychain Access, the developer portal, downloads and base64. The App
Store Connect API covers it, so one script can rotate both secrets in one step.

## Decisions

- **Plain API, no fastlane.** Two resources and a JWT do not justify a Ruby toolchain.
- **Python standard library plus `openssl`** for the JWT (ES256) and the key, CSR and `.p12`; the
  `gh` CLI for the secrets. No new dependencies.
- **Run locally, never in CI.** The script uses an Admin API key, which stays on the developer's
  machine. CI keeps only the notary key it has now.
- **The private key never lands in the repo or stays in a temp folder.** It goes into the `.p12`
  and the secret; importing it into the login keychain for local signing is opt-in.
- **Old certificate is left to expire, never revoked** - revoking would stop every released build
  from launching.

## API

Base `https://api.appstoreconnect.apple.com/v1`, bearer JWT signed with the `.p8` key (issuer id,
key id, 20-minute lifetime, audience `appstoreconnect-v1`).

| Step | Call |
| --- | --- |
| Create certificate | `POST /certificates` - `certificateType: DEVELOPER_ID_APPLICATION_G2`, `csrContent` |

## Script: `scripts/renew-signing.sh` (wrapping `scripts/renew_signing.py`)

```
renew-signing.sh --key-id … --issuer-id … --p8 ~/path/AuthKey.p8 [--import-keychain]
```

1. `openssl` makes an RSA 2048 key and a CSR (`CN=Developer ID Application`, team's details).
2. Create the certificate; build a `.p12` from it and the key with a generated password.
3. `gh secret set` `MACOS_CERT_P12`, `MACOS_CERT_PASSWORD`.
4. With `--import-keychain`, import the `.p12` into the login keychain.
5. Remove every temp file.

## Before starting

- Create an App Store Connect API key with the **Admin** role (Users and Access → Integrations).
  Check whether Apple limits creating Developer ID certificates to the Account Holder; if the API
  refuses, create the certificate in the portal, export the `.p12` and set the two secrets by
  hand.
- Confirm the team can hold a second Developer ID Application certificate (Apple allows two) so
  the new one can be made while the old is still valid.

## Verify

- A `workflow_dispatch` run of `publish.yml` packs, signs and notarises.
- `spctl -a -vv Argonaut.app` accepts it, and the pack's updater check passes.
- The built app launches and opens its saved sources (the key file does not depend on the
  signature, so a new certificate changes nothing there).
