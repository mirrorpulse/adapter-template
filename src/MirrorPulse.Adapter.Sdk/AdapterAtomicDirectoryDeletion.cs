using System.Security.Cryptography;

namespace MirrorPulse.Adapter.Sdk;

/// <summary>Authorizes one atomic deletion of the complete admitted original subtree.</summary>
public sealed record AdapterAtomicDirectoryDeletionRequest(int Version, AdapterDirectoryAdmissionRequest OriginalRequest,
    AdapterDirectoryAdmission Admission, string ManifestFingerprint, string RequestFingerprint)
{
    public void Validate()
    {
        AdapterDirectoryEffects.RequireDigest(RequestFingerprint);
        if (RequestFingerprint != CalculateFingerprint()) throw new InvalidDataException("AtomicDirectoryDeletionRequestChanged");
    }

    public string CalculateFingerprint()
    {
        AdapterDirectoryEffects.RequireVersion(Version);
        if (OriginalRequest is null || Admission is null) throw new InvalidDataException("AtomicDirectoryDeletionAdmissionRequired");
        Admission.RequireRequest(OriginalRequest);
        AdapterDirectoryEffects.RequireDigest(ManifestFingerprint);
        if (OriginalRequest.Operations.Any(step => step.OperationKind != "Delete"))
            throw new NotSupportedException("AtomicDirectoryDeletionRequiresDeletePlan");
        return Convert.ToHexString(SHA256.HashData(AdapterProtocolJson.Encode(new
        {
            kind = "AtomicOriginalDirectoryDeletion",
            Version,
            OriginalRequest.InstanceId,
            OriginalRequest.AdmissionId,
            OriginalRequest.RequestFingerprint,
            Admission.AdmissionFingerprint,
            ManifestFingerprint,
        })));
    }
}

/// <summary>One original removed within the batch's single committed native effect.</summary>
public sealed record AdapterAtomicDirectoryDeletedOriginal(Guid OperationId, AdapterFileAddress Source,
    string OriginalGeneration);

/// <summary>A durable whole-batch deletion receipt. It never represents a provisional individual child effect.</summary>
public sealed record AdapterAtomicDirectoryDeletionReceipt(int Version, Guid InstanceId, Guid AdmissionId, Guid ReceiptId,
    string RequestFingerprint, string AdmissionFingerprint, string ManifestFingerprint,
    IReadOnlyList<AdapterAtomicDirectoryDeletedOriginal> DeletedOriginals, string BackendEvidence)
{
    public void Validate()
    {
        AdapterDirectoryEffects.RequireVersion(Version);
        AdapterDirectoryEffects.RequireId(InstanceId);
        AdapterDirectoryEffects.RequireId(AdmissionId);
        AdapterDirectoryEffects.RequireId(ReceiptId);
        AdapterDirectoryEffects.RequireDigest(RequestFingerprint);
        AdapterDirectoryEffects.RequireDigest(AdmissionFingerprint);
        AdapterDirectoryEffects.RequireDigest(ManifestFingerprint);
        AdapterDirectoryEffects.RequireEvidence(BackendEvidence);
        if (DeletedOriginals is not { Count: >= 1 and <= 2048 })
            throw new InvalidDataException("AtomicDirectoryDeletedOriginalsRequired");
        var operations = new HashSet<Guid>();
        var sources = new HashSet<AdapterFileAddress>();
        foreach (var original in DeletedOriginals)
        {
            if (original?.Source is null) throw new InvalidDataException("AtomicDirectoryDeletedOriginalRequired");
            AdapterDirectoryEffects.RequireId(original.OperationId);
            original.Source.Validate();
            AdapterDirectoryEffects.RequireOpaque(original.OriginalGeneration, 4096);
            if (original.Source.Path.Length == 0 || !operations.Add(original.OperationId) || !sources.Add(original.Source))
                throw new InvalidDataException("AtomicDirectoryDeletedOriginalReused");
        }
    }

    public void RequireRequest(AdapterAtomicDirectoryDeletionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        Validate();
        if (InstanceId != request.OriginalRequest.InstanceId || AdmissionId != request.OriginalRequest.AdmissionId ||
            RequestFingerprint != request.RequestFingerprint || AdmissionFingerprint != request.Admission.AdmissionFingerprint ||
            ManifestFingerprint != request.ManifestFingerprint || DeletedOriginals.Count != request.OriginalRequest.Operations.Count)
            throw new InvalidDataException("AtomicDirectoryDeletionReceiptScopeMismatch");
        for (int ordinal = 0; ordinal < DeletedOriginals.Count; ordinal++)
        {
            var original = DeletedOriginals[ordinal];
            var operation = request.OriginalRequest.Operations[ordinal].Operation;
            if (original.OperationId != operation.OperationId || original.Source != new AdapterFileAddress(operation.RootKey, operation.Path) ||
                original.OriginalGeneration != request.Admission.Operations[ordinal].OriginalGeneration)
                throw new InvalidDataException("AtomicDirectoryDeletionReceiptOriginalMismatch");
        }
    }

    public string Fingerprint()
    {
        Validate();
        return Convert.ToHexString(SHA256.HashData(AdapterProtocolJson.Encode(this)));
    }
}
