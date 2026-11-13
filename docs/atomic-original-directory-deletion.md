# Atomic original directory deletion

A backend that can commit a whole admitted subtree in one native transaction
can offer `atomic-original-directory-delete-v1`, in addition to
`original-directory-admission-v1`. This does not enable the separate individual
child or final-directory effect capabilities.
No default handshake enables it. `DeleteOriginalDirectoryAtomically` returns
`OriginalDirectoryDeletedAtomically`; ordinary `MutationComplete` is insufficient.

The request includes the complete original admission request and backend
admission, and binds their fingerprints to the Host's sealed manifest. All
operations must be conditional Delete operations in the admitted leaf-first
order. Move, upload and directory creation are outside this capability. A staged
cross-instance move may use this operation for source retirement only after the
Host has durably verified the complete destination copy and authorized every
source operation. The capability itself supplies no copy or verification proof.

The backend revalidates its durable admission, complete inventory, original
generations and conditions under the same qualified history and exclusion
boundary as its native transaction. It reserves the immutable Host authorization
before source mutation. The same admission or any of its operation IDs cannot be
rebound to another plan, manifest or individual-effect execution. Capability
negotiation, hashes and SDK shape checks cannot establish these native facts.

All original removals and one immutable receipt commit together. No child result
is returned or counted as accepted before the batch commits. The receipt retains
every operation ID, original address and original generation in the authorized
order, as well as the request, admission and manifest fingerprints and backend
commit evidence. It is a whole-batch receipt; it is not a chain of provisional
`AdapterOwnedChildEffectReceipt` values. It has no fabricated deletion revision.

Before commit, cancellation or process death must preserve every original and
leave no accepted receipt. After commit, a lost reply, cancellation or process
restart must recover the identical receipt without opening or mutating a later
occupant at the original path. The backend refuses changed authorization and
missing, corrupt or incompatible retained evidence. Host acknowledgement follows
durable acceptance of the full receipt and remains a separate product step.

`AdapterAtomicDirectoryDeletionWire` checks protocol, both capabilities,
request/session/instance correlation, a 1 MiB body limit and exact reply membership.
The dispatcher validates before invoking `IAdapterAtomicDirectoryDeletionBackend`
and validates its reply. Signed Host/Worker integration must separately qualify
durability, native history/exclusion, root authorization and official journal ACK.
The SDK contract checks use a synthetic backend and do not claim those outcomes.
