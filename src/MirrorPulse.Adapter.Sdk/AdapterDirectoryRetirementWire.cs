using System.Security.Cryptography;
using System.Text;

namespace MirrorPulse.Adapter.Sdk;

/// <summary>A final nonrecursive directory operation bound to a sealed child ledger and original direct parents.</summary>
/// <remarks>Shape validation does not establish backend exclusion, complete history or permission to acknowledge a journal.</remarks>
public sealed record AdapterDirectoryRetirementRequest(int Version, string OperationKind, AdapterOperationRequest Operation,
    AdapterDirectoryRetirementEvidence Retirement, IReadOnlyList<AdapterDirectoryGenerationWitness> OriginalParents,
    IReadOnlyList<AdapterOwnedChildEffectReceipt> ParentPredecessors, string RequestFingerprint)
{
    public void Validate()
    {
        AdapterDirectoryEffects.RequireDigest(RequestFingerprint);
        if (RequestFingerprint != CalculateFingerprint()) throw new InvalidDataException("DirectoryRetirementRequestChanged");
    }

    /// <summary>Fingerprint every retained dependency and parent predecessor; never substitute a current Stat.</summary>
    public string CalculateFingerprint()
    {
        ValidateShape();
        var canonicalRetirement = Retirement with
        {
            RequiredChildren = Retirement.RequiredChildren.OrderBy(value => value.OperationId.ToString("D"), StringComparer.Ordinal).ToArray(),
            AcceptedChildren = Retirement.AcceptedChildren.Select(value => value with
            {
                Parents = value.Parents.OrderBy(item => item.OriginalParent.Directory.RootKey, StringComparer.Ordinal)
                    .ThenBy(item => item.OriginalParent.Directory.Path, StringComparer.Ordinal).ToArray()
            }).ToArray()
        };
        return Convert.ToHexString(SHA256.HashData(AdapterProtocolJson.Encode(new
        {
            version = Version,
            operationKind = OperationKind,
            operation = Operation,
            retirement = canonicalRetirement,
            originalParents = OriginalParents.Select(AdapterDirectoryEffects.FingerprintWitness).Order(StringComparer.Ordinal).ToArray(),
            parentPredecessors = ParentPredecessors.Select(AdapterDirectoryEffects.Fingerprint).Order(StringComparer.Ordinal).ToArray()
        })));
    }

    /// <summary>Check a typed source receipt before a Host persists it; null deletion revisions remain null.</summary>
    public void RequireAcceptedReceipt(AdapterOwnedChildEffectReceipt receipt)
    {
        Validate(); ArgumentNullException.ThrowIfNull(receipt); receipt.Validate();
        var original = Retirement.OriginalDirectory;
        AdapterDirectoryEffects.RequireOriginalRequest(receipt, original.InstanceId, original.AdmissionId, Operation.OperationId, RequestFingerprint);
        var destination = OperationKind == "Move" ? new AdapterFileAddress(Operation.DestinationRootKey!, Operation.DestinationPath!) : null;
        if (receipt.OperationKind != OperationKind || receipt.Child != original.Directory || receipt.Destination != destination ||
            receipt.OriginalChildGeneration != original.Generation || receipt.Parents.Count != OriginalParents.Count)
            throw new InvalidDataException("DirectoryRetirementReceiptMismatch");
        foreach (var parent in OriginalParents)
        {
            var transition = receipt.Parents.SingleOrDefault(value => value.OriginalParent == parent)
                ?? throw new InvalidDataException("DirectoryRetirementOriginalParentMismatch");
            var predecessor = ParentPredecessors.SingleOrDefault(value => value.Parents.Any(item => item.OriginalParent == parent));
            if (predecessor is not null) AdapterDirectoryEffects.RequirePredecessor(transition, predecessor);
            else if (transition.BeforeRevision != parent.OriginalRevision || transition.PreviousReceiptFingerprint is not null)
                throw new InvalidDataException("DirectoryRetirementOriginalParentRevisionRequired");
        }
    }

    private void ValidateShape()
    {
        AdapterDirectoryEffects.RequireVersion(Version);
        if (OperationKind is not ("Delete" or "Move") || Operation is null || Retirement is null)
            throw new InvalidDataException("DirectoryRetirementOperationRequired");
        Retirement.Validate();
        AdapterProtocolJson.ValidateMutation(Operation, requiresDestination: OperationKind == "Move");
        var original = Retirement.OriginalDirectory;
        if (!Operation.IsDirectory || Operation.RootKey != original.Directory.RootKey || Operation.Path != original.Directory.Path ||
            Operation.Preconditions is null || Operation.Preconditions.ExpectedRevision != Retirement.FinalExpectedRevision ||
            !Operation.Preconditions.DestinationMustBeAbsent || Retirement.RequiredChildren.Any(value => value.OperationId == Operation.OperationId))
            throw new InvalidDataException("OriginalDirectoryRetirementBindingRequired");
        if (OperationKind == "Move" && (Operation.DestinationPath!.Length == 0 || Operation.DestinationRootKey == Operation.RootKey &&
            (Operation.DestinationPath == Operation.Path || Operation.DestinationPath.StartsWith(Operation.Path + "/", StringComparison.Ordinal))))
            throw new InvalidDataException("DirectoryRetirementDestinationForbidden");
        // This also requires the exact source/destination direct-parent set and forbids root mutations.
        _ = AdapterDirectoryEffects.FingerprintRequest(OperationKind, original.InstanceId, original.AdmissionId, Operation, OriginalParents);
        var sourceParent = OriginalParents.Single(value => value.Directory.RootKey == Operation.RootKey &&
            value.Directory.Path == ParentPath(Operation.Path));
        if (sourceParent.Generation != original.ParentGeneration) throw new InvalidDataException("OriginalDirectoryParentGenerationMismatch");
        if (ParentPredecessors is null || ParentPredecessors.Count > OriginalParents.Count)
            throw new InvalidDataException("DirectoryRetirementPredecessorsInvalid");
        var seen = new HashSet<AdapterDirectoryGenerationWitness>();
        var operations = new HashSet<Guid>();
        foreach (var predecessor in ParentPredecessors)
        {
            if (predecessor is null) throw new InvalidDataException("DirectoryRetirementPredecessorRequired");
            predecessor.Validate();
            if (predecessor.InstanceId != original.InstanceId || predecessor.AdmissionId != original.AdmissionId ||
                predecessor.OperationId == Operation.OperationId || !operations.Add(predecessor.OperationId))
                throw new InvalidDataException("DirectoryRetirementPredecessorScopeMismatch");
            var matched = predecessor.Parents.Where(value => OriginalParents.Contains(value.OriginalParent)).ToArray();
            if (matched.Length == 0 || matched.Any(value => !seen.Add(value.OriginalParent)))
                throw new InvalidDataException("DirectoryRetirementPredecessorAmbiguous");
        }
    }

    private static string ParentPath(string path) => path.LastIndexOf('/') is int separator && separator >= 0 ? path[..separator] : string.Empty;
}

/// <summary>Optional protocol-v2 messages; an ordinary v2 handshake never enables this contract.</summary>
public static class AdapterDirectoryRetirementWire
{
    public const string RequestMessage = "RetireOriginalDirectory";
    public const string ResponseMessage = "OriginalDirectoryRetired";
    public const int MaximumPayloadBytes = 1024 * 1024;

    public static AdapterDirectoryRetirementRequest ReadRequest(AdapterControlFrame frame, IEnumerable<string>? negotiatedCapabilities)
    {
        RequireEnvelope(frame, negotiatedCapabilities);
        if (frame.IsResponse || frame.MessageType != RequestMessage) throw new InvalidDataException("DirectoryRetirementRequestEnvelopeInvalid");
        var request = AdapterProtocolJson.Decode<AdapterDirectoryRetirementRequest>(Payload(frame));
        request.Validate();
        if (request.Retirement.OriginalDirectory.InstanceId != frame.InstanceId) throw new InvalidDataException("DirectoryRetirementInstanceMismatch");
        return request;
    }

    public static AdapterOwnedChildEffectReceipt ReadResponse(AdapterControlFrame requestFrame, AdapterControlFrame responseFrame,
        IEnumerable<string>? negotiatedCapabilities)
    {
        var request = ReadRequest(requestFrame, negotiatedCapabilities);
        RequireEnvelope(responseFrame, negotiatedCapabilities);
        if (!responseFrame.IsResponse || responseFrame.MessageType != ResponseMessage || responseFrame.RequestId != requestFrame.RequestId ||
            responseFrame.InstanceId != requestFrame.InstanceId || responseFrame.WorkerSessionId != requestFrame.WorkerSessionId)
            throw new InvalidDataException("DirectoryRetirementResponseEnvelopeInvalid");
        var receipt = AdapterProtocolJson.Decode<AdapterOwnedChildEffectReceipt>(Payload(responseFrame));
        request.RequireAcceptedReceipt(receipt);
        return receipt;
    }

    private static void RequireEnvelope(AdapterControlFrame frame, IEnumerable<string>? capabilities)
    {
        ArgumentNullException.ThrowIfNull(frame);
        AdapterDirectoryEffects.RequireNegotiated(frame.ProtocolVersion, capabilities);
        if (frame.RequestId == Guid.Empty || frame.InstanceId == Guid.Empty || frame.WorkerSessionId == Guid.Empty)
            throw new InvalidDataException("DirectoryRetirementEnvelopeScopeRequired");
    }

    private static byte[] Payload(AdapterControlFrame frame)
    {
        if (frame.Payload.ValueKind != System.Text.Json.JsonValueKind.Object) throw new InvalidDataException("DirectoryRetirementPayloadRequired");
        string text = frame.Payload.GetRawText();
        if (Encoding.UTF8.GetByteCount(text) > MaximumPayloadBytes) throw new InvalidDataException("DirectoryRetirementPayloadTooLarge");
        return Encoding.UTF8.GetBytes(text);
    }
}
