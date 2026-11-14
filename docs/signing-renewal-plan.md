# Signing renewal: scripted certificate and profile rotation

**Status: planned, not started.**

## Why

The macOS release needs three secrets that go stale together:

- `MACOS_CERT_P12` / `MACOS_CERT_PASSWORD` - the Developer ID Application certificate and its
  private key. Certificates last 5 years.
- `MACOS_PROVISION_PROFILE` - the Developer ID provisioning profile that lets the app claim
  `keychain-access-groups` (see `KeychainSealingKeyStore`). It lasts 18 years, but it lists the
  certificates it was issued for, so a new certificate needs a new profile. An app signed with a
  certificate its profile does not list is killed at launch.

Doing that by hand means Keychain Access, the developer portal, downloads and base64. The App
Store Connect API covers all of it, so one script can rotate the three secrets in one step.

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
- **No build-time check that the certificate and profile match.** The user asked for builds not to
  fail on it for now; the script making both together is what keeps them consistent.

## API

Base `https://api.appstoreconnect.apple.com/v1`, bearer JWT signed with the `.p8` key (issuer id,
key id, 20-minute lifetime, audience `appstoreconnect-v1`).

| Step | Call |
| --- | --- |
| Create certificate | `POST /certificates` - `certificateType: DEVELOPER_ID_APPLICATION_G2`, `csrContent` |
| Find the App ID | `GET /bundleIds?filter[identifier]=com.sevanconsulting.argonaut` (registered lower case) |
| Find old profiles | `GET /profiles?filter[profileType]=MAC_APP_DIRECT` |
| Create profile | `POST /profiles` - `profileType: MAC_APP_DIRECT`, relationships `bundleId`, `certificates` |
| Delete old profile | `DELETE /profiles/{id}` |

The created profile's `profileContent` is already base64 - it goes to `gh secret set` as is.

## Script: `scripts/renew-signing.sh` (wrapping `scripts/renew_signing.py`)

```
renew-signing.sh --key-id … --issuer-id … --p8 ~/path/AuthKey.p8 [--profile-only] [--import-keychain]
```

1. `openssl` makes an RSA 2048 key and a CSR (`CN=Developer ID Application`, team's details).
2. Create the certificate; build a `.p12` from it and the key with a generated password.
3. Find the App ID, create the `MAC_APP_DIRECT` profile listing the new certificate, delete the old
   profile.
4. `gh secret set` `MACOS_CERT_P12`, `MACOS_CERT_PASSWORD`, `MACOS_PROVISION_PROFILE`.
5. With `--import-keychain`, import the `.p12` into the login keychain.
6. Remove every temp file.

`--profile-only` skips 1-2 and issues a profile for the newest valid Developer ID certificate -
for when only the profile needs replacing.

## Before starting

- Create an App Store Connect API key with the **Admin** role (Users and Access → Integrations).
  Check whether Apple limits creating Developer ID certificates to the Account Holder; if the API
  refuses, create the certificate in the portal and use `--profile-only`.
- Confirm the team can hold a second Developer ID Application certificate (Apple allows two) so
  the new one can be made while the old is still valid.

## Verify

- A `workflow_dispatch` run of `publish.yml` packs, signs and notarises.
- `codesign -d --entitlements - Argonaut.app` shows the profile's application identifier;
  `spctl -a -vv` accepts it.
- The built app launches, opens its saved sources with no Keychain prompt (the access group is
  the team's, so a new certificate changes nothing there).
