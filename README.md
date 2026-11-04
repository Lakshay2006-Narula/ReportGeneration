# Indoor Report Generator

Production desktop report generator for indoor network survey ZIP logs. The app combines a Next.js frontend, a local ASP.NET Core report API, and a Tauri Windows shell.

## Features

- Upload one or more exported survey ZIP logs.
- Discover available file bands and select the bands to include.
- Generate Excel reports in separate or combined sheet modes.
- Select KPI plot inputs such as RSRP, RSRQ, SINR, DL/UL throughput, CI, EARFCN, LTE BLER, PCI, NodeB ID, and PUSCH TX.
- Track report generation with backend progress polling.
- Save generated Excel reports through the desktop file picker.
- Offline machine-based license activation.

## Tech Stack

- Frontend: Next.js 15, React 19, Tailwind CSS
- Desktop shell: Tauri 2
- Backend: ASP.NET Core 8
- Packaging: Tauri NSIS/MSI targets

## Repository Layout

```text
app/                         Next.js app UI
backend/SignalTracker.Api/   ASP.NET Core local report API
public/                      Web assets
scripts/                     Development helper scripts
src-tauri/                   Tauri desktop shell and Windows packaging config
tools/                       Internal license generator source
```

Generated folders such as `.next/`, `out/`, `backend-publish/`, `src-tauri/target/`, `bin/`, `obj/`, and installer binaries are intentionally ignored.

## Prerequisites

- Node.js 20 or newer
- npm
- .NET SDK 8
- Rust stable toolchain
- Tauri CLI dependencies for Windows
- WebView2 Runtime
- NSIS for installer generation

## Setup

```bash
npm install
```

## Development

Run the web frontend and backend together:

```bash
npm run web:dev
```

Frontend:

```text
http://localhost:3000
```

Backend:

```text
http://127.0.0.1:5000
```

Run inside the Tauri desktop shell:

```bash
npm run desktop:dev
```

## Build

Build the static frontend:

```bash
npm run build
```

Publish the backend:

```bash
npm run build:backend
```

Build the Windows desktop app and installers:

```bash
npm run dist
```

If MSI packaging cannot download WiX in a restricted environment, build the NSIS installer only:

```bash
npm run dist:dir
```

Release outputs are written under:

```text
src-tauri/target/release/
src-tauri/target/release/bundle/
```

## Wi-Fi and cellular reports

Choose **Wi-Fi** or **Cellular** in the header. Online mode lists only image-plot sessions for the selected network type within the licensed company. Wi-Fi sessions are recognized by the session type or saved Wi-Fi network rows. Offline mode accepts the matching original ZIP export and validates it before reporting.

Wi-Fi Excel reports use `WiFi_plot.csv` (or Wi-Fi columns in `image_plot.csv`), the six Wi-Fi PNG maps, and the exported color settings. Available Wi-Fi plots are Band, RSSI, Link Speed, Channel, and DL/UL Throughput; unmatched legend values are listed individually. Existing cellular report plots and separate/combined layouts remain available.

`GET /api/RemoteSessions?captureType=wifi` and `captureType=cellular` filter before pagination. Omitting `captureType` retains the image-plot session list for both types. Discovery and ZIP report endpoints accept an optional `CaptureType` (`wifi`, `cellular`, or `auto`).

## Backend API

The frontend uses these report endpoints:

- `POST /api/ExcelReport/DiscoverBands`
- `POST /api/ExcelReport/StartGenerateFromZip`
- `GET /api/ExcelReport/Progress/{jobId}`
- `GET /api/ExcelReport/Download/{jobId}`
- `POST /api/ExcelReport/GenerateFromZip`

During development the API listens on `127.0.0.1:5000`. Packaged desktop builds start the published backend on `127.0.0.1:48327`.

## Licensing

The backend enforces an offline machine license when `License:Enforce` is enabled in `backend/SignalTracker.Api/appsettings.json`.

The private license signing key must never be committed. The `license-keys/` folder is ignored by Git. Keep `private-key.txt` only in a secure internal location.

Create a license key from a trusted machine:

```bash
dotnet run --project tools/SignalTracker.LicenseTool -- issue --machine MACHINE-ID --company-code 4567 --days 365 --features cellular,wifi --name "Customer Name" --private-key license-keys/private-key.txt
```

The application stores activated licenses using Windows user data protection.

## Versioning

Update all of these files when releasing a new desktop version:

- `package.json`
- `package-lock.json`
- `src-tauri/tauri.conf.json`
- `src-tauri/Cargo.toml`

The current app version is `2.0.2`.

## Production Notes

- Do not commit generated EXE/MSI/ZIP artifacts.
- Do not commit `license-keys/`.
- Build from a clean workspace before distributing installers.
- Prefer `npm run dist:dir` for NSIS-only packaging in locked-down networks.
- Keep the backend API private to loopback addresses only.

### Report feature licensing

Issue keys with `--features cellular`, `--features wifi`, or `--features cellular,wifi`. The interactive generator also requires this selection. These permissions are part of the signed payload, not a frontend preference. License status/activation return `features`; unavailable network tabs are disabled. With license enforcement enabled, remote listing/download and ZIP discovery/generation verify permissions, including auto-detected ZIPs. A mixed report requires both permissions. Expired cached licenses lose access without restarting.

Legacy signed keys without a `features` field retain **Cellular only** access. Wi-Fi requires a newly issued key containing the `wifi` feature. An explicitly empty feature list grants no report access.

### Signing key protection

The internal license generator requires an explicit external signing-key file (`--private-key`). It never embeds a private key, reads a key from its own resources, or accepts private-key text as a command-line argument. Its build rejects the known private-key resource. Keep the signing-key file on the trusted internal issuing machine and exclude the generator and signing keys from customer distributions. `genkeys` refuses to overwrite an existing key pair.

Expiry, machine, company, and report permissions are covered by the ECDSA signature. Editing any of those fields in an old key invalidates the signature; the backend rejects it before accepting the payload. Cached licenses are also checked for expiry on every status/access check. A valid old license retains only its original expiry and permitted features (legacy keys: Cellular only).

Previously built generator EXEs containing the signing key remain sensitive: rebuilding source does not remove the key from those old binaries. They must stay internal. If the signing key or an old generator is disclosed, rotate the signing key, update the app's public key and reissue licenses. An offline app cannot remotely revoke existing signed keys or establish trusted time against a local administrator changing the system clock; those requirements need an online licensing service. The customer verifier's public key alone cannot issue licenses.
