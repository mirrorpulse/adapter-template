using System.Security.Cryptography;

namespace MirrorPulse.Adapter.Sdk;

/// <summary>Retains the latest accepted effect for one explicitly named original parent.</summary>
public sealed record AdapterOriginalParentPredecessor(AdapterDirectoryGenerationWitness Parent, AdapterOwnedChildEffectReceipt Receipt);

/// <summary>One source file effect from a durable admission and the Host's sealed execution manifest.</summary>
public sealed record AdapterOriginalChildEffectRequest(int Version, Guid InstanceId, Guid AdmissionId,
    string AdmissionFingerprint, string ManifestFingerprint, string OperationKind, AdapterOperationRequest Operation,
    string OriginalGeneration, IReadOnlyList<AdapterDirectoryGenerationWitness> OriginalParents,
    IReadOnlyList<AdapterOriginalParentPredecessor> ParentPredecessors, string RequestFingerprint)
{
    public void Validate()
    {
        AdapterDirectoryEffects.RequireDigest(RequestFingerprint);
        if (RequestFingerprint != CalculateFingerprint()) throw new InvalidDataException("OriginalChildEffectRequestChanged");
    }

    public string CalculateFingerprint()
    {
        AdapterDirectoryEffects.RequireVersion(Version);
        AdapterDirectoryEffects.RequireDigest(AdmissionFingerprint);
        AdapterDirectoryEffects.RequireDigest(ManifestFingerprint);
        AdapterDirectoryEffects.RequireOpaque(OriginalGeneration, 4096);
        if (Operation is null || Operation.IsDirectory || OperationKind is not ("Delete" or "Move") ||
            Operation.Preconditions is not { DestinationMustBeAbsent: true, ExpectedRevision: { } revision })
            throw new InvalidDataException("OriginalChildEffectOperationRequired");
        AdapterDirectoryEffects.RequireOpaque(revision, 4096);
        string operationFingerprint = AdapterDirectoryEffects.FingerprintRequest(OperationKind, InstanceId, AdmissionId, Operation, OriginalParents);
        if (ParentPredecessors is null || ParentPredecessors.Count > OriginalParents.Count)
            throw new InvalidDataException("OriginalChildEffectPredecessorsInvalid");
        var parents = new HashSet<AdapterDirectoryGenerationWitness>();
        foreach (var predecessor in ParentPredecessors)
        {
            if (predecessor?.Parent is null || predecessor.Receipt is null ||
                !OriginalParents.Contains(predecessor.Parent) || !parents.Add(predecessor.Parent))
                throw new InvalidDataException("OriginalChildEffectPredecessorsInvalid");
            predecessor.Receipt.Validate();
            if (predecessor.Receipt.InstanceId != InstanceId || predecessor.Receipt.AdmissionId != AdmissionId ||
                predecessor.Receipt.OperationId == Operation.OperationId ||
                !predecessor.Receipt.Parents.Any(transition => transition.OriginalParent == predecessor.Parent))
                throw new InvalidDataException("OriginalChildEffectPredecessorScopeMismatch");
        }
        var previous = ParentPredecessors.Select(value => new
        {
            Parent = AdapterDirectoryEffects.FingerprintWitness(value.Parent),
            Receipt = AdapterDirectoryEffects.Fingerprint(value.Receipt),
        }).OrderBy(value => value.Parent, StringComparer.Ordinal).ToArray();
        return Convert.ToHexString(SHA256.HashData(AdapterProtocolJson.Encode(new
        {
            Version,
            InstanceId,
            AdmissionId,
            AdmissionFingerprint,
            ManifestFingerprint,
            OriginalGeneration,
            operationFingerprint,
            previous,
        })));
    }

    public void RequireAdmission(AdapterDirectoryAdmissionRequest original, AdapterDirectoryAdmission admitted)
    {
        Validate();
        ArgumentNullException.ThrowIfNull(admitted);
        admitted.RequireRequest(original);
        int ordinal = original.Operations.ToList().FindIndex(value => value.Operation.OperationId == Operation.OperationId);
        if (InstanceId != admitted.InstanceId || AdmissionId != admitted.AdmissionId ||
            AdmissionFingerprint != admitted.AdmissionFingerprint || ordinal < 0)
            throw new InvalidDataException("OriginalChildEffectAdmissionMismatch");
        var planned = original.Operations[ordinal];
        var witnessed = admitted.Operations[ordinal];
        if (planned.OperationKind != OperationKind || planned.Operation != Operation || witnessed.Directory is not null ||
            witnessed.OriginalGeneration != OriginalGeneration ||
            !witnessed.OriginalParents.ToHashSet().SetEquals(OriginalParents))
            throw new InvalidDataException("OriginalChildEffectAdmissionMismatch");
        foreach (var previous in ParentPredecessors)
        {
            int prior = original.Operations.ToList().FindIndex(value => value.Operation.OperationId == previous.Receipt.OperationId);
            if (prior < 0 || prior >= ordinal || !admitted.Operations[prior].OriginalParents.Contains(previous.Parent))
                throw new InvalidDataException("OriginalChildEffectPredecessorScopeMismatch");
            var source = original.Operations[prior];
            var witness = admitted.Operations[prior];
            var destination = source.OperationKind == "Move"
                ? new AdapterFileAddress(source.Operation.DestinationRootKey!, source.Operation.DestinationPath!) : null;
            if (previous.Receipt.OperationKind != source.OperationKind || previous.Receipt.Child !=
                new AdapterFileAddress(source.Operation.RootKey, source.Operation.Path) ||
                previous.Receipt.OriginalChildGeneration != witness.OriginalGeneration || previous.Receipt.Destination != destination ||
                previous.Receipt.Parents.Count != witness.OriginalParents.Count ||
                previous.Receipt.Parents.Any(value => !witness.OriginalParents.Contains(value.OriginalParent)))
                throw new InvalidDataException("OriginalChildEffectPredecessorScopeMismatch");
        }
    }

    public void RequireAcceptedReceipt(AdapterOwnedChildEffectReceipt receipt)
    {
        Validate();
        AdapterDirectoryEffects.RequireOriginalRequest(receipt, InstanceId, AdmissionId, Operation.OperationId, RequestFingerprint);
        var destination = OperationKind == "Move" ? new AdapterFileAddress(Operation.DestinationRootKey!, Operation.DestinationPath!) : null;
        if (receipt.OperationKind != OperationKind || receipt.Child != new AdapterFileAddress(Operation.RootKey, Operation.Path) ||
            receipt.OriginalChildGeneration != OriginalGeneration || receipt.Destination != destination ||
            receipt.Parents.Count != OriginalParents.Count)
            throw new InvalidDataException("OriginalChildEffectReceiptMismatch");
        foreach (var parent in OriginalParents)
        {
            var transition = receipt.Parents.SingleOrDefault(value => value.OriginalParent == parent)
                ?? throw new InvalidDataException("OriginalChildEffectParentMismatch");
            var previous = ParentPredecessors.SingleOrDefault(value => value.Parent == parent);
            if (previous is not null) AdapterDirectoryEffects.RequirePredecessor(transition, previous.Receipt);
            else if (transition.PreviousReceiptFingerprint is not null || transition.BeforeRevision != parent.OriginalRevision)
                throw new InvalidDataException("OriginalChildEffectFirstParentRevisionRequired");
        }
    }
}
