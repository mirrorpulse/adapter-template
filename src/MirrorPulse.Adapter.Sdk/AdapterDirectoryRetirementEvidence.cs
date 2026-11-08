namespace MirrorPulse.Adapter.Sdk;

/// <summary>One required original direct-child operation from the Host's already sealed manifest.</summary>
public sealed record AdapterDirectoryChildRequirement(Guid OperationId, string RequestFingerprint,
    AdapterFileAddress Child, string OriginalGeneration);

/// <summary>Validates complete owned dependencies before a backend considers the final nonrecursive parent operation.</summary>
/// <remarks>Validation grants no backend authority: original generation and terminal revision still require an atomic current check.</remarks>
public sealed record AdapterDirectoryRetirementEvidence(int Version, AdapterDirectoryGenerationWitness OriginalDirectory,
    string SealedManifestFingerprint, IReadOnlyList<AdapterDirectoryChildRequirement> RequiredChildren,
    IReadOnlyList<AdapterOwnedChildEffectReceipt> AcceptedChildren, string FinalExpectedRevision, string? TerminalReceiptFingerprint)
{
    public void Validate()
    {
        AdapterDirectoryEffects.RequireVersion(Version);
        if (OriginalDirectory is null) throw new InvalidDataException("OriginalParentWitnessRequired");
        OriginalDirectory.Validate();
        if (OriginalDirectory.Directory.Path.Length == 0) throw new InvalidDataException("RootMutationForbidden");
        AdapterDirectoryEffects.RequireDigest(SealedManifestFingerprint);
        AdapterDirectoryEffects.RequireOpaque(FinalExpectedRevision, 4096);
        if (RequiredChildren is null || AcceptedChildren is null || RequiredChildren.Count > 2048 ||
            AcceptedChildren.Count != RequiredChildren.Count) throw new InvalidDataException("OriginalChildDependenciesIncomplete");
        var required = new Dictionary<Guid, AdapterDirectoryChildRequirement>();
        var names = new HashSet<(string Root, string Path)>();
        foreach (var child in RequiredChildren)
        {
            if (child is null || child.Child is null) throw new InvalidDataException("OriginalChildRequirementMissing");
            AdapterDirectoryEffects.RequireId(child.OperationId); AdapterDirectoryEffects.RequireDigest(child.RequestFingerprint);
            AdapterDirectoryEffects.RequireOpaque(child.OriginalGeneration, 4096); child.Child.Validate();
            int slash = child.Child.Path.LastIndexOf('/'); string parent = slash < 0 ? string.Empty : child.Child.Path[..slash];
            if (child.Child.RootKey != OriginalDirectory.Directory.RootKey || parent != OriginalDirectory.Directory.Path ||
                !required.TryAdd(child.OperationId, child) || !names.Add((child.Child.RootKey, child.Child.Path)))
                throw new InvalidDataException("OriginalChildRequirementScopeMismatch");
        }
        var seen = new HashSet<Guid>(); AdapterOwnedChildEffectReceipt? previous = null;
        foreach (var receipt in AcceptedChildren)
        {
            if (receipt is null) throw new InvalidDataException("AcceptedOriginalChildMissing");
            receipt.Validate();
            if (!required.TryGetValue(receipt.OperationId, out var child) || !seen.Add(receipt.OperationId) ||
                receipt.OperationKind is not ("Delete" or "Move") || receipt.Child != child.Child ||
                receipt.OriginalChildGeneration != child.OriginalGeneration)
                throw new InvalidDataException("AcceptedOriginalChildMismatch");
            AdapterDirectoryEffects.RequireOriginalRequest(receipt, OriginalDirectory.InstanceId,
                OriginalDirectory.AdmissionId, child.OperationId, child.RequestFingerprint);
            var transition = receipt.Parents.SingleOrDefault(value => value.OriginalParent == OriginalDirectory)
                ?? throw new InvalidDataException("AcceptedOriginalParentMissing");
            if (receipt.Destination is { } destination)
            {
                int slash = destination.Path.LastIndexOf('/'); string parent = slash < 0 ? string.Empty : destination.Path[..slash];
                if (destination.RootKey == OriginalDirectory.Directory.RootKey && parent == OriginalDirectory.Directory.Path)
                    throw new InvalidDataException("OriginalChildStillOccupiesDirectory");
            }
            if (previous is null)
            {
                if (transition.PreviousReceiptFingerprint is not null || transition.BeforeRevision != OriginalDirectory.OriginalRevision)
                    throw new InvalidDataException("OriginalParentRevisionRequired");
            }
            else AdapterDirectoryEffects.RequirePredecessor(transition, previous);
            previous = receipt;
        }
        if (previous is null)
        {
            if (FinalExpectedRevision != OriginalDirectory.OriginalRevision || TerminalReceiptFingerprint is not null)
                throw new InvalidDataException("OriginalEmptyParentRevisionRequired");
        }
        else
        {
            var last = previous.Parents.Single(value => value.OriginalParent == OriginalDirectory);
            if (FinalExpectedRevision != last.AfterRevision || TerminalReceiptFingerprint != AdapterDirectoryEffects.Fingerprint(previous))
                throw new InvalidDataException("TerminalOwnedParentTransitionRequired");
        }
    }
}
