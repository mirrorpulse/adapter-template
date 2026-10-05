# Worker protocol v2

The canonical SDK is owned by this repository. The protocol is language neutral.
Control envelopes retain the v1 UTF-8 JSON shape and four-byte little-endian
length prefix (maximum 4 MiB). JSON field names are case sensitive. Unknown
optional fields are ignored; unknown required capabilities or wire versions are
rejected. A v1 address can be read only with an explicit single-root binding.

## Negotiation

Every Worker starts with envelope `protocolVersion: 1`, message `Hello`, and
nonempty request, instance and session IDs. New Workers send `supportedVersions`
(`minimum`, `maximum`) and `capabilities`. Missing version offers indicate v1;
malformed offers are rejected. V2 requires `root-addresses`, `stable-operations`,
`conditional-targets`, `bounded-streams`, and `cancel-ack`. Extra offered
capabilities do not imply Host support. The Host selects the highest common
version and sends Ready using that envelope version. V2 Ready contains
`selectedVersion`, the selected capabilities, `roots` (rootKey, enabled,
configuration), and instance `configuration`. The Worker validates Ready before
switching the channel version. All later frames use the selected version.

A v1 Ready retains the legacy configuration dictionary. A new Worker receiving
this shape binds only its single implicit root. A new Host rejects a v1 Worker
when more than one root is configured, including disabled roots: silently
choosing the first enabled root is unsafe. `HandshakeRejected` is a v1 response
with `code` and closes the session. Unsupported mandatory capabilities do not
downgrade to v1. Root settings and credentials remain Host owned.

## Addresses and operations

Every v2 file request and response contains a nonempty `rootKey`. It identifies
one configured root within the instance; the same path or remote ID in another
root identifies another object. Relative paths use `/`, reject parent traversal,
absolute paths, backslashes and control characters. The empty path identifies
the root itself. Workers reject unconfigured roots before accessing storage.

Mutations include a durable nonempty `operationId`, independent of the transport
request ID. Retries reuse this operation ID. Move adds `destinationRootKey` and
`destinationPath`; a destination root is never inferred from a source path.
`preconditions.expectedRevision` checks the source, and
`preconditions.destinationMustBeAbsent` defaults to true. A Worker must reject
unsupported preconditions before modifying either side. CreateDirectory carries
`operationId`, `rootKey`, `path`, and `mustBeAbsent` (default true).

ReadRange carries `rootKey`, `path`, `offset`, `length` and optional
`expectedRevision`. Responses repeat the root and operation IDs where applicable.
Golden JSON examples are in `vectors/`. They are normative field encodings;
JSON object property order is not required for incoming messages.

## Process and framing

The Host starts one executable per enabled instance with six argument tokens:
`--instance-id GUID --worker-session-id GUID --pipe-name NAME`. IDs are nonempty
UUIDs written in the canonical hyphenated string form. The Host supplies
`MP_FILE_CACHE_DIR` and `MP_TRANSFER_CACHE_DIR` as disposable cache grants.
There is no persistent settings directory. Configuration arrives in Ready;
credentials use the separately authorized credential-request exchange. The
Worker must not log credential values. The pipe is local, current-user scoped;
the production Host verifies its peer process as well as instance/session IDs.

Each frame is a UInt32 little-endian payload length followed by exactly that
many bytes, never exceeding 4,194,304 bytes. Reads may be fragmented. A truncated
prefix or payload, excess length, invalid encoding or foreign session terminates
the connection. There is one reader and serialized complete-frame writes in
each direction. Never cancel halfway through a frame and then send another frame
on the same connection. The Host's v2 writes and cancellation acknowledgment
waits have five-second bounds; failure closes the session.

Control payloads are UTF-8 JSON objects with these case-sensitive envelope keys:

| Key | Type | Rule |
| --- | --- | --- |
| protocolVersion | integer | 1 for bootstrap Hello; selected version thereafter |
| messageType | string | exact message name |
| requestId | UUID string | unique, nonempty transport correlation |
| instanceId | UUID string | exact launched instance |
| workerSessionId | UUID string | exact launched session; changes on restart |
| isResponse | boolean | true for a reply to a request |
| payload | object | message-specific fields |

Ready repeats Hello's request ID. Connected is a Worker event (`isResponse:false`)
with a fresh request ID. Stop is a Host command with an empty payload; the Worker
releases pending leases and exits. There is no required Stop acknowledgment.

## File command and response table

All rows below require the common root/path address unless explicitly stated.
Every response uses its request's transport ID, instance/session and source root.
Mutation replies also repeat the durable operationId, including failures.

| Command | Additional payload fields | Success response and fields |
| --- | --- | --- |
| Stat | none | StatResult: rootKey, revision (string or null when absent) |
| List | pageSize (1..512), cursor (opaque base64 string or null) | DirectoryPage: rootKey, entries array, isComplete, cursor |
| ReadRange | offset (nonnegative Int64), length (1..1,048,576), optional expectedRevision | ReadRangeReady: rootKey, streamId, length; immediately followed by its final binary chunk |
| Upload | operationId, streamId, length (nonnegative Int64), preconditions | UploadReady: rootKey, operationId, streamId; after all accepted chunks, UploadComplete: rootKey, operationId, revision |
| Move | operationId, destinationRootKey, destinationPath, preconditions, isDirectory | MutationComplete: rootKey, operationId, revision |
| Delete | operationId, preconditions, isDirectory | MutationComplete: rootKey, operationId, revision (null after deletion) |
| CreateDirectory | operationId, mustBeAbsent (defaults true) | MutationComplete: rootKey, operationId, revision |

List entries carry `remoteId`, `remoteRevision`, `itemKind` (`File` or `Directory`),
`relativePath`, optional `length` and `isDeleted`. Paths are relative to the root;
remote IDs are opaque Worker identifiers. A cursor is bound to root, parent
directory and the provider's pagination semantics; never reuse it in another
root. Incomplete pages require a next cursor. Providers must document limits and
snapshot guarantees. The memory example's index cursor does not pin a generation
across concurrent modifications; it is not a production remote-change feed.

Replay binding compares semantic inputs. JSON property order, fresh transport IDs
and ignored optional fields must not change that binding. Changing an accepted
operation's roots, destination, conditions or other effective input is rejected.

In v2, legacy optional aliases (`sourcePath`, top-level `expectedRevision`) may
be sent for compatibility, but `path`, `destinationRootKey` and `preconditions`
are authoritative. Unsupported directory moves or replacement policies must
return an explicit error before modifying storage. `expectedRevision:null`
means the source/target is expected absent when creating; a nonnull revision is
compared against the existing source. `destinationMustBeAbsent:true` forbids
overwriting the destination. A provider may reject a less restrictive requested
policy; it must not silently use a weaker condition. ReadRange's expectedRevision
is checked when supplied; omission does not assert a revision-pinned read.

`operationId` binds the entire mutation (roots, paths, preconditions, length and
content), independently of fresh request/stream IDs. A retried accepted operation
returns its original receipt. Reusing the ID with different inputs is an error.
This identifier is not an unconditional exactly-once guarantee: production
providers must retain/recover acceptance evidence or explicitly report ambiguity
after restart. The example retains only 256 receipts in memory for its current
session; Host durable recovery remains a separate product responsibility.

## Binary v2 payload layout

The length prefix covers the complete following payload. All integers are little
endian. UUID bytes use .NET Guid byte order: the first UInt32, UInt16 and UInt16
fields are little endian, followed by the eight remaining bytes in textual order
(Python `UUID.bytes_le`). A golden vector is in `vectors/binary-v2.json`.

| Offset | Bytes | Meaning |
| --- | --- | --- |
| 0 | 4 | ASCII MPB2 |
| 4 | 2 | UInt16 byte length R of rootKey |
| 6 | R | strict UTF-8 rootKey; R=1..1024, root length <=256 UTF-16 code units |
| 6+R | 16 each | requestId, instanceId, workerSessionId, streamId |
| 70+R | 8 | nonnegative Int64 byte offset |
| 78+R | 4 | UInt32 content byte count N, at most 1,048,576 |
| 82+R | 1 | flags: bit0 EndOfStream, bit1 SHA256 present; all other bits zero |
| 83+R | 32 | mandatory raw SHA256 of content bytes |
| 115+R | N | exact content bytes, no trailing data |

V2 requires bit1. Stream correlation includes all four IDs and rootKey. Chunks
must arrive at the next expected offset and must not exceed the declared range
or upload length. The final flag must coincide with the declared end. Reject
duplicate/out-of-order, short final, excess and checksum-invalid chunks. A
zero-length upload uses one empty final chunk with SHA256 of empty bytes. An
empty nonfinal chunk is invalid. Range reads are bounded and use one final chunk;
uploads may use multiple chunks. Streaming validation need not buffer the entire
file; sample limits are independent of the wire's Int64 total length.

## Cancellation and failure

Cancel has a fresh requestId and payload `rootKey`, `targetRequestId`, and optional
`operationId`. It has no path. For an active upload, the Worker validates the
root/operation binding, stops accepting bytes, removes the transfer lease, sends
the target's terminal OperationError (`code:Canceled`), then replies CancelAck
with `rootKey`, `targetRequestId`, `status:canceled`. After an already completed
request it replies with `status:alreadyCompleted`. Cancellation is not rollback
of an accepted remote mutation; the Host uses its durable operation ID to recover
a lost acceptance receipt. A Worker must multiplex control and binary frames
while accepting an upload. CancelAck must not be sent before lease cleanup.

Read completion may race consumer cancellation; the production Host drains and
validates the bounded range before issuing another range. Providers that cannot
interrupt a bounded synchronous read may finish it before handling Cancel.
Transport abort or Stop must clean remaining leases. No response from an earlier
session may be adopted by a new one.

OperationError has `rootKey`, mutation `operationId` when applicable, and a fixed
`code`; it must not contain credentials or raw exception text. Common errors are
UnknownRoot, RootOffline, RemoteConflict, NotFound, DestinationExists,
OperationUnsupported, OperationBindingMismatch and Canceled. A malformed frame,
foreign correlation or invalid binary stream closes the session. Extra optional
JSON fields may be ignored; unknown protocol versions and missing required v2
capabilities are never silently accepted.

## Downloaded conformance runner

Each SDK Release supplies native self-contained x64 and ARM64 runners, the SDK
package, this specification with vectors, and `sdk-release.json` containing
source SHA, fixed asset names, lengths and SHA256. Consumers pin the release tag
and hashes; neither latest nor an overwritten asset is an acceptable SDK source.
The publication workflow rejects an existing version instead of replacing it.

Run `MirrorPulse.Adapter.Conformance.exe --worker C:/Path/Worker.exe --transfer-cache C:/Temporary/Cache`.
Use an empty disposable cache. This executes the controlled **memory-source
profile**, not arbitrary user storage: roots left/right/offline, initial
readme.txt with UTF-8 `Memory source: ROOT\n`, disabled-root rejection, bounded
read/list, a two-chunk upload, mid-upload cancellation, cross-root move and retry,
create-directory and delete. Provider-specific conformance must supply a test
source with these semantics or an equivalent provider fixture; this runner alone
does not certify a real storage protocol, remote polling or crash recovery.

An optional `--worker-argument PATH` inserts one argument before the six startup
tokens, enabling the test-only Python wire implementation. It imports no SDK.
`eng/verify-wire-conformance.ps1` runs that actual process and deliberate wrong-root,
missing-capability and broken-cancellation variants. Scripts are test fixtures;
installed Adapter packages still contain compiled EXEs and dependencies.
