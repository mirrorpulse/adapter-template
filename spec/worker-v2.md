# Worker protocol v2

The canonical SDK is owned by this repository. The protocol is language neutral.
Control envelopes retain the v1 UTF-8 JSON shape and four-byte little-endian
length prefix (maximum 4 MiB). JSON field names are case sensitive. Unknown
optional fields are ignored; unknown required capabilities or wire versions are
rejected. A v1 address can be read only with an explicit single-root binding.

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
