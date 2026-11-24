# MirrorPulse Adapter template

Use this template to build an Adapter Worker for MirrorPulse. The example exposes a single in-memory storage root. Clone the repository, change the package identity and presentation, then replace the sample storage implementation.

## Build a package

Install PowerShell 7 and the .NET SDK selected by `global.json`. On Windows, double-click `Build-Adapter.cmd`. Use `./build-adapter.sh` on macOS or Linux to cross-build both Windows Worker targets. The launchers restore locked dependencies, build x64 and ARM64, create a `.mpadapter`, and sign and verify it with a disposable test key. Find the resulting path in the output under `artifacts/release`.

Run `pwsh ./Build-Adapter.ps1 -Version 1.0.0-preview.1` to choose a package version. Use the protected release workflow for publication. The development launcher also accepts `-ReleaseSignature` when the signing environment is already configured.

## Customize the Adapter

1. Edit `eng/release-manifest.json`. Set your Adapter ID, publisher, and single root key. Keep that root key stable across updates.
2. Edit `template/package/forms/config.xml` and `template/package/locales/en-US.json`. Replace `template/package/assets/logo.png` with your own local logo. The package signature covers these files.
3. Implement your storage behavior in `samples/MirrorPulse.Adapter.SampleWorker`. Use the documented SDK handshake and keep deployment settings and credentials in MirrorPulse.
4. Run `pwsh ./eng/verify.ps1` to validate and build the sample, then use a Windows runner to verify its signed Worker. MirrorPulse retains ownership of actual user configuration, credentials and deployments.

The canonical SDK, protocol and conformance tools live in the independent SDK repository. The sample uses the exact SDK version declared in its project and lock file. [Adapter SDK](https://github.com/mirrorpulse/adapter-sdk) · [Adapter documentation](https://mirrorpulse.github.io/adapter/)
