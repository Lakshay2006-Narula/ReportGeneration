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
dotnet run --project tools/SignalTracker.LicenseTool -- issue --machine MACHINE-ID --days 365 --name "Customer Name" --private-key license-keys/private-key.txt
```

The application stores activated licenses using Windows user data protection.

## Versioning

Update all of these files when releasing a new desktop version:

- `package.json`
- `package-lock.json`
- `src-tauri/tauri.conf.json`
- `src-tauri/Cargo.toml`

The current app version is `2.0.1`.

## Production Notes

- Do not commit generated EXE/MSI/ZIP artifacts.
- Do not commit `license-keys/`.
- Build from a clean workspace before distributing installers.
- Prefer `npm run dist:dir` for NSIS-only packaging in locked-down networks.
- Keep the backend API private to loopback addresses only.
