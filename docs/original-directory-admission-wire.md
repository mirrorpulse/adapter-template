# Original directory admission and child effects

A Worker negotiates `original-directory-admission-v1` before accepting
`AdmitOriginalDirectory`, which returns `OriginalDirectoryAdmitted`.
Individual `ApplyOriginalChild` / `OriginalChildApplied` exchanges additionally
require `original-directory-effects-v1` and `original-directory-child-effects-v1`.
The existing `RetireOriginalDirectory` message completes an admitted directory
after its children under its existing effects capability. Admission alone grants
neither mutation path.

The Host sends the original instance, admission ID and complete ordered source
operations. Every entry retains its stable operation ID, address, explicit
revision condition, destination for a move and absence condition. Children
precede their direct parent and all paths form one connected subtree. Move
destinations leave that original subtree. The request fingerprint covers the
entire ordered plan. The plan has at most 2048 operations.

The backend checks its complete inventory and original conditions and persists
the admission before replying. The reply binds each operation to its original
generation, a directory witness where appropriate and the exact source and
destination parent witnesses. Witnesses retain the admission request fingerprint.
The admission fingerprint covers those observations. Duplicate requests recover
that same admission. A changed request or incomplete inventory is refused.
The backend retains the stable operation claims across admissions.

Each file effect request retains the admission and Host manifest fingerprints,
original operation and generation, original parent witnesses and the previously
accepted receipt for each affected parent. Its fingerprint includes these
bindings. The backend validates the request against its durable admission and
checks each predecessor against its accepted effect ledger. Shape validation
by itself cannot establish those backend facts.
The backend binds the Host manifest once per admission and requires the same
binding for every child effect and final directory retirement.

The typed child receipt binds the full request fingerprint, original generation
and exact before/after transitions for each original parent. The first effect
starts from the admitted revision. Later effects extend the previously accepted
receipt. A deletion's accepted revision is null. The backend commits the native
effect and durable receipt together and returns that receipt after a lost reply
or process restart. The Host persists the same receipt before advancing its plan.

`AdapterOriginalDirectoryWire` validates protocol, negotiated capabilities,
bounded payloads and request/session/instance correlation. Each body is limited
to 1 MiB. `AdapterOriginalDirectoryDispatcher` decodes before calling the backend
and validates the backend reply before returning a control frame. A Worker can
route these messages from its existing control reader to an
`IAdapterOriginalDirectoryBackend` implementation. An admission-only backend can
implement `IAdapterOriginalDirectoryAdmissionBackend`; the dispatcher refuses
individual effects when that implementation is unavailable.

Backend implementations also own root authorization, native path rules,
complete history, exclusion during validation and mutation, and crash recovery.
The dispatcher supports protocol validation around that implementation.
Production enablement requires signed Host and Worker qualification of the
complete admitted operation and official journal acknowledgement.

A backend that only commits an entire original subtree atomically can use the
separately negotiated [atomic deletion contract](atomic-original-directory-deletion.md).
It must not return an individual child receipt while that child's native
transaction can still roll back.
