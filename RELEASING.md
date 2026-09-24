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
- `pendant/InitPrompt.c`
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

State the minimum KMotion version in the release notes — **5.4.4**, the first public release with
the trajectory-planner SET/GET fix the init relies on. A user on an older KMotion can update, or
fall back to building from source.

**You do NOT have to build against that minimum version.** Build with whatever KMotion you have
installed (point `<KMotionRoot>` at it). `KMotion_dotNet.dll` carries the same assembly identity
across releases — 1.0.1.0 in 5.4.0, 5.4.3 and 5.4.5 alike — and the zip deliberately excludes it,
so the shipped exe binds to whichever copy already sits in the user's own `Release64`. The 5.4.4
floor comes from the KFLOP-side trajectory-planner fix, not from anything the build links against.

(v1.0.1 was built against 5.4.5 on this basis. The earlier "build against the minimum" rule was
precautionary and turned out to block releases on keeping an old KMotion installed.)
