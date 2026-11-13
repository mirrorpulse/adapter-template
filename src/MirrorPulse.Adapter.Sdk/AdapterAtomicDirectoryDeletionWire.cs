using System.Text;
using System.Text.Json;

namespace MirrorPulse.Adapter.Sdk;

/// <summary>An optional whole-subtree transaction, negotiated independently from individual child effects.</summary>
public static class AdapterAtomicDirectoryDeletionWire
{
    public const string Capability = "atomic-original-directory-delete-v1";
    public const string RequestMessage = "DeleteOriginalDirectoryAtomically";
    public const string ResponseMessage = "OriginalDirectoryDeletedAtomically";

    public static AdapterAtomicDirectoryDeletionRequest ReadRequest(AdapterControlFrame frame, IEnumerable<string>? capabilities)
    {
        RequireEnvelope(frame, capabilities);
        if (frame.IsResponse || frame.MessageType != RequestMessage)
            throw new InvalidDataException("AtomicDirectoryDeletionWireRequestInvalid");
        var request = AdapterProtocolJson.Decode<AdapterAtomicDirectoryDeletionRequest>(Payload(frame));
        request.Validate();
        if (request.OriginalRequest.InstanceId != frame.InstanceId)
            throw new InvalidDataException("AtomicDirectoryDeletionWireInstanceMismatch");
        return request;
    }

    public static AdapterAtomicDirectoryDeletionReceipt ReadResponse(AdapterControlFrame requestFrame, AdapterControlFrame responseFrame,
        IEnumerable<string>? capabilities)
    {
        var request = ReadRequest(requestFrame, capabilities);
        RequireEnvelope(responseFrame, capabilities);
        if (!responseFrame.IsResponse || responseFrame.MessageType != ResponseMessage ||
            responseFrame.RequestId != requestFrame.RequestId || responseFrame.InstanceId != requestFrame.InstanceId ||
            responseFrame.WorkerSessionId != requestFrame.WorkerSessionId)
            throw new InvalidDataException("AtomicDirectoryDeletionWireResponseMismatch");
        var receipt = AdapterProtocolJson.Decode<AdapterAtomicDirectoryDeletionReceipt>(Payload(responseFrame));
        receipt.RequireRequest(request);
        return receipt;
    }

    private static void RequireEnvelope(AdapterControlFrame frame, IEnumerable<string>? capabilities)
    {
        ArgumentNullException.ThrowIfNull(frame);
        string[] negotiated = capabilities?.ToArray() ?? [];
        if (frame.ProtocolVersion != 2 || !negotiated.Contains(AdapterOriginalDirectoryWire.Capability, StringComparer.Ordinal) ||
            !negotiated.Contains(Capability, StringComparer.Ordinal))
            throw new NotSupportedException("AtomicDirectoryDeletionUnsupported");
        if (frame.RequestId == Guid.Empty || frame.InstanceId == Guid.Empty || frame.WorkerSessionId == Guid.Empty)
            throw new InvalidDataException("AtomicDirectoryDeletionWireScopeRequired");
    }

    private static byte[] Payload(AdapterControlFrame frame)
    {
        if (frame.Payload.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("AtomicDirectoryDeletionWirePayloadRequired");
        string json = frame.Payload.GetRawText();
        if (Encoding.UTF8.GetByteCount(json) > AdapterDirectoryRetirementWire.MaximumPayloadBytes)
            throw new InvalidDataException("AtomicDirectoryDeletionWirePayloadTooLarge");
        return Encoding.UTF8.GetBytes(json);
    }
}

/// <summary>Commits all admitted deletions and their one immutable receipt together, then replays that receipt after restart.</summary>
public interface IAdapterAtomicDirectoryDeletionBackend
{
    ValueTask<AdapterAtomicDirectoryDeletionReceipt> DeleteAtomicallyAsync(AdapterAtomicDirectoryDeletionRequest request,
        CancellationToken cancellationToken);
}

public static class AdapterAtomicDirectoryDeletionDispatcher
{
    public static async ValueTask<AdapterControlFrame> DispatchAsync(AdapterControlFrame frame,
        IEnumerable<string>? negotiatedCapabilities, IAdapterAtomicDirectoryDeletionBackend backend, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backend);
        cancellationToken.ThrowIfCancellationRequested();
        string[] capabilities = negotiatedCapabilities?.ToArray() ?? [];
        var request = AdapterAtomicDirectoryDeletionWire.ReadRequest(frame, capabilities);
        var receipt = await backend.DeleteAtomicallyAsync(request, cancellationToken).ConfigureAwait(false);
        var response = frame with
        {
            IsResponse = true,
            MessageType = AdapterAtomicDirectoryDeletionWire.ResponseMessage,
            Payload = AdapterProtocolJson.ToElement(receipt),
        };
        // A committed effect remains accepted even if the caller cancels while the reply is being delivered.
        _ = AdapterAtomicDirectoryDeletionWire.ReadResponse(frame, response, capabilities);
        return response;
    }
}
