# SDK trusted publishing

The SDK follows [CfSharp's version and release rules](https://github.com/MirrorPulse/CfSharp):
stable versions increment major/minor/patch according to the merged develop PR's
single `breaking`/`feature`/`fix` label; explicitly requested develop previews use
`X.Y.Z-preview.N`. Published NuGet and GitHub versions jointly establish the
baseline, including the already published GitHub SDK `0.2.0`.

## Owner setup

Create nuget.org trusted publishing policies with these fields:

| Field | Stable | Preview |
| --- | --- | --- |
| Repository owner | `MirrorPulse` | `MirrorPulse` |
| Repository | `adapter-template` | `adapter-template` |
| Workflow file | `sdk-release.yml` | `sdk-release.yml` |
| Environment | `stable` | `preview` |
| Package pattern | `MirrorPulse.Adapter.Sdk` | `MirrorPulse.Adapter.Sdk` |

Allow publication of new packages and new versions as appropriate for the
package owner. Set the GitHub secret `NUGET_USER` to that nuget.org profile name,
not its email address. No persistent NuGet API key or new signing certificate is
required. See the [official trusted publishing setup](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing).

The job uses the same pinned `NuGet/login` action as CfSharp and requests an OIDC
token only after package verification and environment approval. The exchanged
key is passed through the environment and is not recorded in evidence.

## Existing SDK 0.2.0

Dispatch `SDK Release` on `main`, select `Stable`, set `existing_version` to
`0.2.0`, `publish` to `true`, and `confirm` to `PUBLISH`. A permitted actor other
than the stable environment approver must initiate the run, since self review is
disabled. The workflow downloads the existing GitHub artifacts, validates tag,
source SHA, package metadata, licenses and SHA256, and pushes the original
`.nupkg` without rebuilding or replacing GitHub assets. With `publish=false`, the
same download and validation perform a dry run without OIDC or publication.

## New versions

Merging a classified develop PR into main starts stable preparation and both
native conformance gates, then waits for the protected stable environment.
Previews require an explicit dispatch from develop, `Preview`, `publish=true`
and `PUBLISH`. Dispatches default to dry runs. SDK publication is separate from
provider `.mpadapter` signing and application Store submission.

NuGet is published before the matching GitHub Release, using the same package.
Neither service is a cross-service transaction: inspect retained version,
package and source evidence if one publication succeeds and the next fails.
Duplicate versions fail explicitly; there is no automatic overwrite, version
skip or approval bypass. NuGet may repository-sign its copy, changing the outer
archive bytes; the product's GitHub pin continues to identify the original file.
