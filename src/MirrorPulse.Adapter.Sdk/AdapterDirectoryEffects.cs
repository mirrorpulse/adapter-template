using System.Security.Cryptography;

namespace MirrorPulse.Adapter.Sdk;

/// <summary>Immutable backend evidence for one original directory generation and its admitted parent.</summary>
public sealed record AdapterDirectoryGenerationWitness(int Version, Guid InstanceId, Guid AdmissionId, Guid WitnessId,
    AdapterFileAddress Directory, string Generation, string OriginalRevision, string? ParentGeneration,
    string RequestFingerprint, string BackendEvidence)
{
    public void Validate()
    {
        AdapterDirectoryEffects.RequireVersion(Version);
        AdapterDirectoryEffects.RequireId(InstanceId); AdapterDirectoryEffects.RequireId(AdmissionId); AdapterDirectoryEffects.RequireId(WitnessId);
        if (Directory is null) throw new InvalidDataException("DirectoryWitnessAddressRequired");
        Directory.Validate();
        AdapterDirectoryEffects.RequireOpaque(Generation, 4096); AdapterDirectoryEffects.RequireOpaque(OriginalRevision, 4096);
        if (Directory.Path.Length > 0) AdapterDirectoryEffects.RequireOpaque(ParentGeneration, 4096);
        else if (ParentGeneration is not null) throw new InvalidDataException("RootWitnessParentForbidden");
        AdapterDirectoryEffects.RequireDigest(RequestFingerprint); AdapterDirectoryEffects.RequireEvidence(BackendEvidence);
    }
}

/// <summary>One accepted transition of an original direct parent, produced within the backend's atomic boundary.</summary>
public sealed record AdapterOwnedParentTransition(AdapterDirectoryGenerationWitness OriginalParent,
    string BeforeRevision, string AfterRevision, string? PreviousReceiptFingerprint, string BackendEvidence)
{
    public void Validate()
    {
        if (OriginalParent is null) throw new InvalidDataException("OriginalParentWitnessRequired");
        OriginalParent.Validate();
        AdapterDirectoryEffects.RequireOpaque(BeforeRevision, 4096); AdapterDirectoryEffects.RequireOpaque(AfterRevision, 4096);
        if (PreviousReceiptFingerprint is not null) AdapterDirectoryEffects.RequireDigest(PreviousReceiptFingerprint);
        else if (BeforeRevision != OriginalParent.OriginalRevision) throw new InvalidDataException("OriginalParentRevisionRequired");
        AdapterDirectoryEffects.RequireEvidence(BackendEvidence);
    }
}

/// <summary>Retains an original child effect and every direct-parent transition separately from its nullable mutation result.</summary>
public sealed record AdapterOwnedChildEffectReceipt(int Version, Guid InstanceId, Guid AdmissionId, Guid ReceiptId,
    Guid OperationId, string OperationKind, string RequestFingerprint, AdapterFileAddress Child,
    string OriginalChildGeneration, string? AcceptedRevision, IReadOnlyList<AdapterOwnedParentTransition> Parents,
    AdapterFileAddress? Destination = null)
{
    public void Validate()
    {
        AdapterDirectoryEffects.RequireVersion(Version);
        AdapterDirectoryEffects.RequireId(InstanceId); AdapterDirectoryEffects.RequireId(AdmissionId);
        AdapterDirectoryEffects.RequireId(ReceiptId); AdapterDirectoryEffects.RequireId(OperationId);
        if (OperationKind is not ("CreateDirectory" or "Upload" or "Move" or "Delete")) throw new InvalidDataException("DirectoryEffectKindUnsupported");
        AdapterDirectoryEffects.RequireDigest(RequestFingerprint); AdapterDirectoryEffects.RequireOpaque(OriginalChildGeneration, 4096);
        if (Child is null) throw new InvalidDataException("ChildEffectAddressRequired");
        Child.Validate(); if (Child.Path.Length == 0) throw new InvalidDataException("RootMutationForbidden");
        if (OperationKind != "Delete") AdapterDirectoryEffects.RequireOpaque(AcceptedRevision, 4096);
        else if (AcceptedRevision is not null) throw new InvalidDataException("DeletionRevisionMustRemainNull");
        if (Parents is not { Count: >= 1 and <= 2 } || OperationKind != "Move" && Parents.Count != 1)
            throw new InvalidDataException("DirectoryEffectParentsRequired");
        var parents = new HashSet<(string Root, string Path)>();
        foreach (var transition in Parents)
        {
            if (transition is null) throw new InvalidDataException("NullDirectoryEffectParent");
            transition.Validate(); var witness = transition.OriginalParent;
            if (witness.InstanceId != InstanceId || witness.AdmissionId != AdmissionId ||
                !parents.Add((witness.Directory.RootKey, witness.Directory.Path))) throw new InvalidDataException("DirectoryEffectParentScopeMismatch");
        }
        int separator = Child.Path.LastIndexOf('/'); string parent = separator < 0 ? string.Empty : Child.Path[..separator];
        if (!parents.Contains((Child.RootKey, parent))) throw new InvalidDataException("OriginalDirectParentRequired");
        if (OperationKind == "Move")
        {
            if (Destination is null) throw new InvalidDataException("DirectoryEffectDestinationRequired");
            Destination.Validate(); if (Destination.Path.Length == 0) throw new InvalidDataException("RootMutationForbidden");
            int destinationSeparator = Destination.Path.LastIndexOf('/');
            string destinationParent = destinationSeparator < 0 ? string.Empty : Destination.Path[..destinationSeparator];
            var expected = new HashSet<(string Root, string Path)> { (Child.RootKey, parent), (Destination.RootKey, destinationParent) };
            if (!parents.SetEquals(expected)) throw new InvalidDataException("DirectoryEffectDestinationParentMismatch");
        }
        else if (Destination is not null) throw new InvalidDataException("DirectoryEffectUnexpectedDestination");
    }
}

/// <summary>Validates optional evidence without inferring capability, backend atomicity or current mutation authority.</summary>
public static class AdapterDirectoryEffects
{
    public const int Version = 1;
    public const string Capability = "original-directory-effects-v1";

    public static void RequireNegotiated(int protocolVersion, IEnumerable<string>? capabilities)
    {
        if (protocolVersion != 2 || capabilities is null || !capabilities.Contains(Capability, StringComparer.Ordinal))
            throw new NotSupportedException("OriginalDirectoryEffectsUnsupported");
    }

    /// <summary>Correlates a successor with the exact original parent and immutable predecessor receipt.</summary>
    public static void RequirePredecessor(AdapterOwnedParentTransition next, AdapterOwnedChildEffectReceipt previous)
    {
        ArgumentNullException.ThrowIfNull(next); ArgumentNullException.ThrowIfNull(previous);
        next.Validate(); previous.Validate();
        var parent = previous.Parents.SingleOrDefault(item => item.OriginalParent == next.OriginalParent)
            ?? throw new InvalidDataException("DirectoryEffectOriginalParentMismatch");
        if (next.BeforeRevision != parent.AfterRevision || next.PreviousReceiptFingerprint != Fingerprint(previous))
            throw new InvalidDataException("DirectoryEffectPredecessorMismatch");
    }

    /// <summary>Hashes the complete immutable typed receipt; never use a current parent read as a replacement.</summary>
    public static string Fingerprint(AdapterOwnedChildEffectReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt); receipt.Validate();
        var canonical = receipt with
        {
            Parents = receipt.Parents.OrderBy(value => value.OriginalParent.Directory.RootKey, StringComparer.Ordinal)
            .ThenBy(value => value.OriginalParent.Directory.Path, StringComparer.Ordinal).ToArray()
        };
        return Convert.ToHexString(SHA256.HashData(AdapterProtocolJson.Encode(canonical)));
    }

    public static string FingerprintWitness(AdapterDirectoryGenerationWitness witness)
    {
        ArgumentNullException.ThrowIfNull(witness); witness.Validate();
        return Convert.ToHexString(SHA256.HashData(AdapterProtocolJson.Encode(witness)));
    }

    public static string FingerprintRequest(string operationKind, Guid instanceId, Guid admissionId,
        AdapterOperationRequest operation, IReadOnlyList<AdapterDirectoryGenerationWitness> parents)
    {
        RequireId(instanceId); RequireId(admissionId); ArgumentNullException.ThrowIfNull(operation);
        AdapterProtocolJson.ValidateMutation(operation, requiresDestination: operationKind == "Move");
        if (operationKind is not ("CreateDirectory" or "Upload" or "Move" or "Delete") || parents is not { Count: >= 1 and <= 2 })
            throw new InvalidDataException("DirectoryEffectRequestUnsupported");
        var unique = new HashSet<(string Root, string Path)>();
        foreach (var witness in parents)
        {
            if (witness is null) throw new InvalidDataException("OriginalParentWitnessRequired");
            witness.Validate();
            if (witness.InstanceId != instanceId || witness.AdmissionId != admissionId ||
                !unique.Add((witness.Directory.RootKey, witness.Directory.Path))) throw new InvalidDataException("DirectoryEffectParentScopeMismatch");
        }
        int slash = operation.Path.LastIndexOf('/');
        var expected = new HashSet<(string Root, string Path)> { (operation.RootKey, slash < 0 ? string.Empty : operation.Path[..slash]) };
        if (operationKind == "Move")
        {
            int destinationSlash = operation.DestinationPath!.LastIndexOf('/');
            expected.Add((operation.DestinationRootKey!, destinationSlash < 0 ? string.Empty : operation.DestinationPath[..destinationSlash]));
        }
        else if (operation.DestinationPath is not null || operation.DestinationRootKey is not null)
            throw new InvalidDataException("DirectoryEffectUnexpectedDestination");
        if (operation.Path.Length == 0 || !unique.SetEquals(expected)) throw new InvalidDataException("DirectoryEffectOriginalParentsRequired");
        var ordered = parents.OrderBy(value => value.Directory.RootKey, StringComparer.Ordinal)
            .ThenBy(value => value.Directory.Path, StringComparer.Ordinal).Select(FingerprintWitness).ToArray();
        return Convert.ToHexString(SHA256.HashData(AdapterProtocolJson.Encode(new
        {
            version = Version,
            instanceId,
            admissionId,
            operationKind,
            operation,
            originalParents = ordered
        })));
    }

    public static void RequireOriginalRequest(AdapterOwnedChildEffectReceipt receipt, Guid instanceId, Guid admissionId,
        Guid operationId, string requestFingerprint)
    {
        ArgumentNullException.ThrowIfNull(receipt); receipt.Validate(); RequireDigest(requestFingerprint);
        if (receipt.InstanceId != instanceId || receipt.AdmissionId != admissionId || receipt.OperationId != operationId ||
            receipt.RequestFingerprint != requestFingerprint) throw new InvalidDataException("DirectoryEffectRequestMismatch");
    }

    internal static void RequireVersion(int version) { if (version != Version) throw new NotSupportedException("DirectoryEffectVersionUnsupported"); }
    internal static void RequireId(Guid value) { if (value == Guid.Empty) throw new InvalidDataException("DirectoryEffectIdentityRequired"); }
    internal static void RequireDigest(string? value)
    {
        if (value is not { Length: 64 } || value.Any(character => character is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
            throw new InvalidDataException("DirectoryEffectFingerprintRequired");
    }
    internal static void RequireOpaque(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            throw new InvalidDataException("DirectoryEffectOpaqueValueRequired");
    }
    internal static void RequireEvidence(string? value)
    {
        if (value is null || value.Length is 0 or > 87384) throw new InvalidDataException("DirectoryEffectEvidenceRequired");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(value); }
        catch (FormatException error) { throw new InvalidDataException("DirectoryEffectEvidenceInvalid", error); }
        if (bytes.Length is 0 or > 65536 || Convert.ToBase64String(bytes) != value) throw new InvalidDataException("DirectoryEffectEvidenceInvalid");
    }
}
