using System.Security.Cryptography;

namespace MirrorPulse.Adapter.Sdk;

/// <summary>One immutable source operation in a leaf-first original subtree plan.</summary>
public sealed record AdapterOriginalDirectoryOperation(string OperationKind, AdapterOperationRequest Operation);

/// <summary>Requests durable original generations for every operation before the first source mutation.</summary>
public sealed record AdapterDirectoryAdmissionRequest(int Version, Guid InstanceId, Guid AdmissionId,
    IReadOnlyList<AdapterOriginalDirectoryOperation> Operations, string RequestFingerprint)
{
    public void Validate()
    {
        AdapterDirectoryEffects.RequireDigest(RequestFingerprint);
        if (RequestFingerprint != CalculateFingerprint()) throw new InvalidDataException("DirectoryAdmissionRequestChanged");
    }

    public string CalculateFingerprint()
    {
        AdapterDirectoryEffects.RequireVersion(Version);
        AdapterDirectoryEffects.RequireId(InstanceId);
        AdapterDirectoryEffects.RequireId(AdmissionId);
        if (Operations is not { Count: >= 1 and <= 2048 } || Operations[^1]?.Operation is not { IsDirectory: true } root)
            throw new InvalidDataException("OriginalDirectoryAdmissionPlanRequired");

        var sources = new Dictionary<AdapterFileAddress, int>();
        var operations = new HashSet<Guid>();
        var destinations = new HashSet<AdapterFileAddress>();
        var subtree = new AdapterFileAddress(root.RootKey, root.Path);
        for (int ordinal = 0; ordinal < Operations.Count; ordinal++)
        {
            var step = Operations[ordinal];
            if (step?.Operation is null || step.OperationKind is not ("Delete" or "Move"))
                throw new InvalidDataException("OriginalDirectoryAdmissionOperationRequired");
            var operation = step.Operation;
            AdapterProtocolJson.ValidateMutation(operation, step.OperationKind == "Move");
            if (operation.Path.Length == 0 || operation.Preconditions is not { DestinationMustBeAbsent: true, ExpectedRevision: { } revision })
                throw new InvalidDataException("OriginalDirectoryAdmissionPreconditionRequired");
            AdapterDirectoryEffects.RequireOpaque(revision, 4096);
            if (!operations.Add(operation.OperationId) ||
                !sources.TryAdd(new(operation.RootKey, operation.Path), ordinal))
                throw new InvalidDataException("OriginalDirectoryAdmissionOperationReused");

            if (step.OperationKind == "Move")
            {
                var destination = new AdapterFileAddress(operation.DestinationRootKey!, operation.DestinationPath!);
                if (destination.Path.Length == 0 || !destinations.Add(destination) ||
                    destination.RootKey == subtree.RootKey && (destination.Path == subtree.Path ||
                        destination.Path.StartsWith(subtree.Path + "/", StringComparison.Ordinal)))
                    throw new InvalidDataException("OriginalDirectoryAdmissionDestinationForbidden");
            }
            else if (operation.DestinationRootKey is not null || operation.DestinationPath is not null)
                throw new InvalidDataException("DirectoryEffectUnexpectedDestination");
        }
        for (int ordinal = 0; ordinal < Operations.Count - 1; ordinal++)
        {
            var parent = Parent(Operations[ordinal].Operation);
            if (!sources.TryGetValue(parent, out int parentOrdinal) || parentOrdinal <= ordinal ||
                !Operations[parentOrdinal].Operation.IsDirectory)
                throw new InvalidDataException("OriginalDirectoryAdmissionChildOrderRequired");
        }
        return Convert.ToHexString(SHA256.HashData(AdapterProtocolJson.Encode(new { Version, InstanceId, AdmissionId, Operations })));
    }

    internal static AdapterFileAddress Parent(AdapterOperationRequest operation)
    {
        int separator = operation.Path.LastIndexOf('/');
        return new(operation.RootKey, separator < 0 ? string.Empty : operation.Path[..separator]);
    }
}

/// <summary>Backend observations for one exact planned source operation.</summary>
public sealed record AdapterAdmittedDirectoryOperation(Guid OperationId, string OriginalGeneration,
    AdapterDirectoryGenerationWitness? Directory, IReadOnlyList<AdapterDirectoryGenerationWitness> OriginalParents);

/// <summary>A replayable backend admission which retains every original generation and direct-parent witness.</summary>
public sealed record AdapterDirectoryAdmission(int Version, Guid InstanceId, Guid AdmissionId, string RequestFingerprint,
    IReadOnlyList<AdapterAdmittedDirectoryOperation> Operations, string AdmissionFingerprint)
{
    public string CalculateFingerprint()
    {
        AdapterDirectoryEffects.RequireVersion(Version);
        AdapterDirectoryEffects.RequireId(InstanceId);
        AdapterDirectoryEffects.RequireId(AdmissionId);
        AdapterDirectoryEffects.RequireDigest(RequestFingerprint);
        if (Operations is not { Count: >= 1 and <= 2048 }) throw new InvalidDataException("OriginalDirectoryAdmissionEvidenceRequired");
        var ids = new HashSet<Guid>();
        foreach (var operation in Operations)
        {
            if (operation is null || operation.OriginalParents is not { Count: >= 1 and <= 2 } || !ids.Add(operation.OperationId))
                throw new InvalidDataException("OriginalDirectoryAdmissionEvidenceInvalid");
            AdapterDirectoryEffects.RequireId(operation.OperationId);
            AdapterDirectoryEffects.RequireOpaque(operation.OriginalGeneration, 4096);
            foreach (var parent in operation.OriginalParents) RequireScope(parent);
            if (operation.Directory is { } directory) RequireScope(directory);
        }
        var ordered = Operations.Select(operation => operation with
        {
            OriginalParents = operation.OriginalParents.OrderBy(parent => parent.Directory.RootKey, StringComparer.Ordinal)
                .ThenBy(parent => parent.Directory.Path, StringComparer.Ordinal).ToArray()
        }).ToArray();
        return Convert.ToHexString(SHA256.HashData(AdapterProtocolJson.Encode(new
        {
            Version,
            InstanceId,
            AdmissionId,
            RequestFingerprint,
            Operations = ordered,
        })));
    }

    public void RequireRequest(AdapterDirectoryAdmissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        AdapterDirectoryEffects.RequireDigest(AdmissionFingerprint);
        if (AdmissionFingerprint != CalculateFingerprint() || InstanceId != request.InstanceId || AdmissionId != request.AdmissionId ||
            RequestFingerprint != request.RequestFingerprint || Operations.Count != request.Operations.Count)
            throw new InvalidDataException("OriginalDirectoryAdmissionRequestMismatch");
        var witnesses = new Dictionary<AdapterFileAddress, AdapterDirectoryGenerationWitness>();
        for (int ordinal = 0; ordinal < Operations.Count; ordinal++)
        {
            var admitted = Operations[ordinal];
            var original = request.Operations[ordinal];
            var operation = original.Operation;
            if (admitted.OperationId != operation.OperationId || operation.IsDirectory != (admitted.Directory is not null))
                throw new InvalidDataException("OriginalDirectoryAdmissionOperationMismatch");
            _ = AdapterDirectoryEffects.FingerprintRequest(original.OperationKind, InstanceId, AdmissionId, operation, admitted.OriginalParents);
            foreach (var parent in admitted.OriginalParents)
                if (witnesses.TryGetValue(parent.Directory, out var existing) && parent != existing)
                    throw new InvalidDataException("OriginalDirectoryAdmissionParentChanged");
                else witnesses[parent.Directory] = parent;

            if (admitted.Directory is { } directory)
            {
                var parent = admitted.OriginalParents.Single(value => value.Directory == AdapterDirectoryAdmissionRequest.Parent(operation));
                if (directory.Directory != new AdapterFileAddress(operation.RootKey, operation.Path) ||
                    directory.Generation != admitted.OriginalGeneration || directory.ParentGeneration != parent.Generation ||
                    directory.OriginalRevision != operation.Preconditions!.ExpectedRevision)
                    throw new InvalidDataException("OriginalDirectoryAdmissionGenerationMismatch");
            }
        }
        foreach (var admitted in Operations.Where(value => value.Directory is not null))
            if (witnesses.TryGetValue(admitted.Directory!.Directory, out var asParent) && asParent != admitted.Directory)
                throw new InvalidDataException("OriginalDirectoryAdmissionParentChanged");
    }

    private void RequireScope(AdapterDirectoryGenerationWitness witness)
    {
        if (witness is null) throw new InvalidDataException("OriginalParentWitnessRequired");
        witness.Validate();
        if (witness.InstanceId != InstanceId || witness.AdmissionId != AdmissionId || witness.RequestFingerprint != RequestFingerprint)
            throw new InvalidDataException("OriginalDirectoryAdmissionWitnessScopeMismatch");
    }
}
