# Original directory generations and owned child effects, version 1

This optional protocol-v2 extension defines evidence for a sealed original
directory and its direct-child effects. The SDK validates correlation and shape;
it does not establish backend atomicity. The base v2 handshake does not advertise
`original-directory-effects-v1`. A Worker must implement and qualify the entire
backend contract before advertising that exact capability. Version 1 and unknown
versions remain unsupported by default. Existing mutation JSON is unchanged.

## Original witness

`AdapterDirectoryGenerationWitness` binds the Host instance, admission, witness,
root/address, opaque original generation and revision, original parent generation,
canonical admission request fingerprint and bounded backend evidence. A root has
no parent generation; every other directory requires one. A path is only a
locator. An inode/file ID, creation timestamp, directory timestamp or current
empty listing alone does not prove an unreplaced original generation.

The backend must explain how its evidence rejects delete/recreate, reparent,
rename, cross-root substitution and generation reuse. The Host retains the
complete sealed child manifest and original witness before dispatch. It may not
refresh the original witness after a child changes the parent's revision.

## Accepted effects

`AdapterOwnedChildEffectReceipt` is an immutable accepted child operation with its
exact original operation/request fingerprint, original child generation, nullable
mutation result and direct-parent transitions. Delete keeps its own accepted
revision null. That null does not represent a parent generation or transition.
Moves bind both original source and destination direct parents, with a single
transition when those parents coincide. Unexpected/missing/duplicate parents,
foreign roots/admissions and incomplete destination evidence are invalid.

Each transition binds the complete original parent witness, the parent's accepted
before/after revisions, an optional exact predecessor receipt fingerprint and
backend evidence. The first starts at the sealed original revision. Later
transitions extend the exact previous accepted receipt. `RequirePredecessor`
compares the complete original witness, revision and receipt fingerprint.

The child mutation and accepted parent transition must share a backend atomic or
exclusion boundary. A child write followed by an unprotected parent stat is
insufficient. An unrelated contender changing the parent must be excluded or
cause the operation/receipt to remain unaccepted. A receipt cannot adopt a later
version merely because a directory is now empty.

## Canonical fingerprints

All fingerprints use SHA-256 with 64 uppercase hexadecimal digits. The SDK uses
its typed camel-case JSON encoding with omitted nulls. Parent arrays are ordered
by root key then path using ordinal string ordering before hashing. The original
request binds extension version, instance/admission, exact operation kind,
complete original operation/preconditions and ordered original witness hashes.
Changing an original revision, destination, operation ID, witness or generation
changes its fingerprint. Backend evidence is canonical base64 with at most64KiB
decoded bytes. Opaque generation/revision fields are at most4096 characters and
cannot contain control characters. Witness/operation/admission IDs are nonempty.

## Final parent mutation and recovery

The Host must retain every required original child operation and accepted receipt
before submitting the final nonrecursive parent mutation. Missing, failed,
ambiguous or unsupported children prevent parent execution and journal ACK.
The Worker must atomically verify the original parent generation and terminal
owned-effect transition before the final mutation. A new unowned child must
prevent retirement. Current emptiness cannot replace original dependencies.

`AdapterDirectoryRetirementEvidence` validates the Host's complete set of original
direct-child operation IDs, request fingerprints, addresses and generations
against an ordered accepted chain. It refuses missing, repeated, foreign,
reordered or replaced dependencies, a same-parent rename that still occupies the
directory, and a terminal revision/fingerprint that differs from the owned head.
An originally empty directory retains its original revision and no invented
terminal receipt. Each nested directory still requires its own sealed dependency
set; the direct-parent validator does not silently prove a whole recursive tree.

Backend receipt persistence and outcome recovery are mandatory. A lost reply,
Worker restart or Host restart must recover the original accepted operation or
retain ambiguity. Historical receipts do not grant current mutation authority.
A replay must not refresh timestamps, choose another generation, mutate a
replacement or fabricate a parent transition from a new stat. Final parent
mutation, local namespace retirement and Host journal ACK are separate facts.

This change supplies public evidence types and deterministic validators. It does
not implement a storage backend, new live message handler, Host executor, durable
receipt storage or recursive deletion. A Worker must not advertise this capability
merely because it consumes this SDK. Those implementations and their exact wire
request/reply handlers must be reviewed and tested together before enabling it.

## Required backend conformance

- A signed single-root Worker completes an original nested manifest leaf-first,
  retaining complete target/source bytes and the exact original dependency chain.
- Another process cannot have its create/delete/rename/metadata/replacement effect
  incorporated into an accepted original transition. Test same-path generation
  replacement and a removed child made absent by someone else.
- A failed child prevents parent deletion and ACK even when the parent is empty.
- Every mutation, persistence, reply, reconnect and final ACK cut has independent
  process evidence. Unknown outcomes remain pending without another write.
- Damaged, reordered, duplicate, cross-root, foreign-generation and version/null
  evidence is refused without effects; disabled roots are never probed.
- Existing file, legacy-v1 and ordinary-v2 behavior and signed process fixtures
  remain unchanged. Contract unit checks alone do not qualify a backend.

No credentials, native handles or user content belong in receipts or ordinary
logs. The Host owns persistent recovery data; any Worker state location needs an
explicit Host grant and documented cleanup/retention rules.
