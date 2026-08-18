# Cutting a release

Prebuilt binaries let users run the bridge without installing the .NET SDK or building from
source (README → Quick start → "Download & run"). To publish one:

### 1. Build in Release

```
dotnet build pendant/iMachKflop.csproj -c Release
```

(This also deploys into your local `<KMotion>\KMotion\Release64` — harmless.)

### 2. Assemble the zip

The runtime files only — none of KMotion's own DLLs.

From `pendant/bin/Release/net48/`:
- `iMachKflop.exe`
- `iMachKflop.exe.config`
- `LibUsbDotNet.LibUsbDotNet.dll` (and any other `.dll` in that folder; skip `*.pdb` / `*.xml`)

Plus, from the repo:
- `pendant/PendantService.c`
- `pendant/EStopWatch.c`
- `pendant/pendant.conf`
- `shared/KflopToKMotionCNCFunctions.c`

**Do NOT include `KMotion_dotNet.dll`.** It ships with the user's KMotion install and is
version-matched to it; the bridge loads it from `<KMotion>\KMotion\Release64` — which is
exactly where the user extracts this zip. The build deliberately does not copy it into the
output, so as long as you zip only the files listed above you're fine.

Name it e.g. `imach-p4s-pendant-vX.Y.Z.zip`. The user extracts its contents straight into
their `<KMotion>\KMotion\Release64` folder.

### 3. Tag and publish

GitHub → **Releases → Draft a new release** → create a tag (e.g. `v1.0.0`), add a title and
highlights, **attach the zip**, and publish.

### Note the KMotion version

State in the release notes which KMotion version you built and tested against (currently
**KMotion 5.4.2+**, plus Tom Kerekes' patched `KMotionCNC.exe` for the trajectory-planner
SET/GET — see [pendant/docs/INSTALL_GUIDE.md](pendant/docs/INSTALL_GUIDE.md)). A user whose
KMotion is older, or whose .NET wrapper API differs, can always fall back to building from
source.
