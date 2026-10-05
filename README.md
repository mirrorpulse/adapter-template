# MirrorPulse Adapters

This repository template is the starting point for an independently packaged MirrorPulse Adapter Worker.

## Canonical SDK

This repository owns `MirrorPulse.Adapter.Sdk`. Contract changes and package
versions originate here. Other repositories consume a pinned package; source
copies are compatibility snapshots until their package migration is complete.
Version `0.2.0` preserves the v1 runtime API while introducing the v2 contract.
SDK versions follow semantic versioning independently of Adapter package versions.
Breaking wire changes require a new negotiated protocol version.

Run `pwsh ./eng/verify-sdk-package.ps1` to pack the public library, inspect its
inventory, and restore, lock and run a fresh consumer using only a local feed.
The SDK package contains the public library and metadata, with no Worker,
Host database, configuration, signing key or user data.

## Layout

- `src/` contains reusable Worker SDK code.
- `samples/` contains a minimal executable Worker.
- `template/` contains the manifest and `.mpadapter` package skeleton.
- `eng/` contains repository validation and packaging scripts.

Adapters communicate with MirrorPulse over the current-user Named Pipe contract and receive configuration, credentials references, source-directory grants, and cache paths from MirrorPulse at runtime.

The template does not implement a storage protocol. Provider repositories should add their own protocol code and publish a signed `.mpadapter` release.

## Release reference

`eng/release-settings.json` selects the Worker project. `eng/release-manifest.json`
owns package metadata. Version and tag inputs enter scripts only as environment
data and must be canonical numeric versions. Every referenced Action uses a full
commit SHA. Build runs without signing secrets; sign and publish use separate
jobs, environments and permissions. Manual dispatch defaults to signing artifacts
without creating tags or Releases.

Configure the `adapter-signing` and `adapter-release` environments with trusted
branch/tag rules, required reviewers and scoped signing secrets before enabling
production publishing. Naming an environment in YAML does not configure those
protections. The existing organization signing secrets remain supported until
the repository owner migrates their scope.

Run `pwsh ./eng/verify-release.ps1` for hostile version rejection and a dual-RID
package signed with an in-memory disposable key. It never reads a private key
file or publishes a Release. The packaging verifier checks the embedded signature
and inventory against the exported public key; product installation independently
checks publisher trust. This example retains the current framework-dependent v1
Worker. Self-contained runtime/SDK evolution belongs to the next contract stage.
