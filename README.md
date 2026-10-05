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

The executable sample implements a bounded, ephemeral memory source. Each enabled root starts with `readme.txt`; its bytes identify that root. All source content and retry receipts disappear when the process exits. Provider repositories replace this demonstration source with their storage implementation.

## Release reference

`eng/release-settings.json` selects the Worker project. `eng/release-manifest.json`
owns package metadata. Version and tag inputs enter scripts only as environment
data and must be canonical `X.Y.Z` or `X.Y.Z-preview.N` versions. Existing
four-part numeric identities are preserved when validating old packages. Every referenced Action uses a full
commit SHA. Build runs without signing secrets; sign and publish use separate
jobs, environments and permissions. Manual dispatch defaults to signing artifacts
without creating tags or Releases.

`eng/resolve-adapter-version.ps1` resolves the next stable or preview version
from published GitHub Releases using the CfSharp increment policy. Preview
counters are compared numerically; previews are published as prereleases and
cannot replace the stable `latest` release. The package filename, manifest and
release version must agree before signing or publication. Provider publication
entry points and protected channels are migrated separately from this version contract.

Configure the `adapter-signing` and `adapter-release` environments with trusted
branch/tag rules, required reviewers and scoped signing secrets before enabling
production publishing. Naming an environment in YAML does not configure those
protections. The existing organization signing secrets remain supported until
the repository owner migrates their scope.

Run `pwsh ./eng/verify-release.ps1` for hostile version rejection and a dual-RID
package signed with an in-memory disposable key. It never reads a private key
file or publishes a Release. The packaging verifier checks the embedded signature
and inventory against the exported public key; product installation independently
checks publisher trust. The example uses v2 exclusively because it declares multiple roots. The SDK
retains v1 for explicitly negotiated single-root Workers. Transfer files are
temporary leases under the Host-provided cache; there is no persistent settings
directory. The sample limits files to 2 MiB, source content to 8 MiB, nodes to
4096, roots to 64, concurrent uploads to four and in-session retry receipts to
256. Directory moves are explicitly unsupported.

CI runs on native x64 and ARM64 Windows runners. It copies only tracked source
into a fresh Git repository, restores locked dependencies, builds and executes
the sample, creates a dual-RID package with an ephemeral test signature, and
installs it through a pinned MirrorPulse production catalog. The production
Supervisor then exercises both roots and mutations in the installed Worker.
Run `pwsh ./eng/verify-generated-template.ps1 -ProductRepositoryPath C:/Path/To/MirrorPulse`
to reproduce this gate. No Cloud Files registration is needed for this check.

## Worker protocol and SDK releases

The language-neutral contract is in [spec/worker-v2.md](spec/worker-v2.md), with
JSON and binary golden vectors. `eng/verify-wire-conformance.ps1` exercises a
test-only Python process that imports no SDK and detects wrong-root, missing
capability and broken-cancellation variants. Python 3 is required for this gate.

`SDK Release` follows the [CfSharp branch model](docs/repository-policy.md):
classified develop PRs merged into main prepare stable releases, and explicit
develop dispatches prepare `X.Y.Z-preview.N` previews. Manual dispatch defaults
to a dry run. Both native x64 and ARM64 conformance gates run before new versions
publish. NuGet trusted publishing uses GitHub OIDC through `NuGet/login`, with
the `stable` or `preview` environment and the `NUGET_USER` profile-name secret.
See [trusted publishing setup](docs/nuget-publishing.md) for the exact policy
fields and mirroring existing SDK `0.2.0` without rebuilding its original package.
The resulting `sdk-vVERSION` Release contains one `.nupkg`, two native runner
archives, the specification/vectors archive and `sdk-release.json` with source
SHA, asset names, lengths and SHA256. Existing tags and assets are never replaced.
Downstream consumers pin those hashes before adding the package to a local feed.
The conformance runner covers the controlled memory-source profile; a provider
repository also needs its own real-storage and crash-recovery tests.
