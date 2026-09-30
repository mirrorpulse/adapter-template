# `.mpadapter` package layout

The package root is a ZIP archive with the `.mpadapter` extension:

```text
manifest.json
worker/
  win-x64/MirrorPulse.Adapter.Worker.exe
  win-arm64/MirrorPulse.Adapter.Worker.exe
locales/
  en-US.json
META-INF/mirrorpulse/signature.json
```

The signature entry contains the signed inventory of every other package file,
so a downloaded package can be installed by itself. Package entries use
forward-slash relative paths and are hashed before signing. Older packages may
use a neighboring `.signature.json` file; release tooling should publish both
during the transition.
