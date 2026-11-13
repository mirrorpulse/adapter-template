using System.Text;
using System.Text.Json;

namespace MirrorPulse.Adapter.Sdk;

/// <summary>Negotiated messages for acquiring original directory evidence and executing admitted file effects.</summary>
public static class AdapterOriginalDirectoryWire
{
    public const string Capability = "original-directory-admission-v1";
    public const string ChildCapability = "original-directory-child-effects-v1";
    public const string AdmissionRequestMessage = "AdmitOriginalDirectory";
    public const string AdmissionResponseMessage = "OriginalDirectoryAdmitted";
    public const string ChildRequestMessage = "ApplyOriginalChild";
    public const string ChildResponseMessage = "OriginalChildApplied";

    public static AdapterDirectoryAdmissionRequest ReadAdmissionRequest(AdapterControlFrame frame, IEnumerable<string>? capabilities)
    {
        RequireRequest(frame, AdmissionRequestMessage, capabilities);
        var request = AdapterProtocolJson.Decode<AdapterDirectoryAdmissionRequest>(Payload(frame));
        request.Validate();
        if (request.InstanceId != frame.InstanceId) throw new InvalidDataException("OriginalDirectoryWireInstanceMismatch");
        return request;
    }

    public static AdapterDirectoryAdmission ReadAdmissionResponse(AdapterControlFrame requestFrame, AdapterControlFrame responseFrame,
        IEnumerable<string>? capabilities)
    {
        var request = ReadAdmissionRequest(requestFrame, capabilities);
        RequireResponse(requestFrame, responseFrame, AdmissionResponseMessage, capabilities);
        var admitted = AdapterProtocolJson.Decode<AdapterDirectoryAdmission>(Payload(responseFrame));
        admitted.RequireRequest(request);
        return admitted;
    }

    public static AdapterOriginalChildEffectRequest ReadChildRequest(AdapterControlFrame frame, IEnumerable<string>? capabilities)
    {
        RequireRequest(frame, ChildRequestMessage, capabilities);
        string[] negotiated = capabilities?.ToArray() ?? [];
        AdapterDirectoryEffects.RequireNegotiated(frame.ProtocolVersion, negotiated);
        if (!negotiated.Contains(ChildCapability, StringComparer.Ordinal))
            throw new NotSupportedException("OriginalDirectoryChildEffectUnsupported");
        var request = AdapterProtocolJson.Decode<AdapterOriginalChildEffectRequest>(Payload(frame));
        request.Validate();
        if (request.InstanceId != frame.InstanceId) throw new InvalidDataException("OriginalDirectoryWireInstanceMismatch");
        return request;
    }

    public static AdapterOwnedChildEffectReceipt ReadChildResponse(AdapterControlFrame requestFrame, AdapterControlFrame responseFrame,
        IEnumerable<string>? capabilities)
    {
        var request = ReadChildRequest(requestFrame, capabilities);
        RequireResponse(requestFrame, responseFrame, ChildResponseMessage, capabilities);
        var receipt = AdapterProtocolJson.Decode<AdapterOwnedChildEffectReceipt>(Payload(responseFrame));
        request.RequireAcceptedReceipt(receipt);
        return receipt;
    }

    private static void RequireRequest(AdapterControlFrame frame, string message, IEnumerable<string>? capabilities)
    {
        RequireEnvelope(frame, capabilities);
        if (frame.IsResponse || frame.MessageType != message) throw new InvalidDataException("OriginalDirectoryWireRequestInvalid");
    }

    private static void RequireResponse(AdapterControlFrame request, AdapterControlFrame response, string message,
        IEnumerable<string>? capabilities)
    {
        RequireEnvelope(response, capabilities);
        if (!response.IsResponse || response.MessageType != message || response.RequestId != request.RequestId ||
            response.InstanceId != request.InstanceId || response.WorkerSessionId != request.WorkerSessionId)
            throw new InvalidDataException("OriginalDirectoryWireResponseMismatch");
    }

    private static void RequireEnvelope(AdapterControlFrame frame, IEnumerable<string>? capabilities)
    {
        ArgumentNullException.ThrowIfNull(frame);
        string[] negotiated = capabilities?.ToArray() ?? [];
        if (frame.ProtocolVersion != 2 || !negotiated.Contains(Capability, StringComparer.Ordinal))
            throw new NotSupportedException("OriginalDirectoryAdmissionUnsupported");
        if (frame.RequestId == Guid.Empty || frame.InstanceId == Guid.Empty || frame.WorkerSessionId == Guid.Empty)
            throw new InvalidDataException("OriginalDirectoryWireScopeRequired");
    }

    private static byte[] Payload(AdapterControlFrame frame)
    {
        if (frame.Payload.ValueKind != JsonValueKind.Object) throw new InvalidDataException("OriginalDirectoryWirePayloadRequired");
        string json = frame.Payload.GetRawText();
        if (Encoding.UTF8.GetByteCount(json) > AdapterDirectoryRetirementWire.MaximumPayloadBytes)
            throw new InvalidDataException("OriginalDirectoryWirePayloadTooLarge");
        return Encoding.UTF8.GetBytes(json);
    }
}

/// <summary>A backend validates and persists complete original admissions without implying mutation capability.</summary>
public interface IAdapterOriginalDirectoryAdmissionBackend
{
    ValueTask<AdapterDirectoryAdmission> AdmitAsync(AdapterDirectoryAdmissionRequest request, CancellationToken cancellationToken);
}

/// <summary>A backend persists admissions, validates its retained originals, and commits each effect and receipt atomically.</summary>
public interface IAdapterOriginalDirectoryBackend : IAdapterOriginalDirectoryAdmissionBackend
{
    ValueTask<AdapterOwnedChildEffectReceipt> ApplyChildAsync(AdapterOriginalChildEffectRequest request, CancellationToken cancellationToken);
    ValueTask<AdapterOwnedChildEffectReceipt> RetireAsync(AdapterDirectoryRetirementRequest request, CancellationToken cancellationToken);
}

/// <summary>Validates negotiated requests before invoking the backend and validates its correlated reply.</summary>
public static class AdapterOriginalDirectoryDispatcher
{
    public static async ValueTask<AdapterControlFrame> DispatchAsync(AdapterControlFrame frame,
        IEnumerable<string>? negotiatedCapabilities, IAdapterOriginalDirectoryAdmissionBackend backend, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(backend);
        cancellationToken.ThrowIfCancellationRequested();
        string[] capabilities = negotiatedCapabilities?.ToArray() ?? [];
        if (frame.MessageType == AdapterOriginalDirectoryWire.AdmissionRequestMessage)
        {
            var request = AdapterOriginalDirectoryWire.ReadAdmissionRequest(frame, capabilities);
            var admitted = await backend.AdmitAsync(request, cancellationToken).ConfigureAwait(false);
            var response = frame with
            {
                IsResponse = true,
                MessageType = AdapterOriginalDirectoryWire.AdmissionResponseMessage,
                Payload = AdapterProtocolJson.ToElement(admitted),
            };
            _ = AdapterOriginalDirectoryWire.ReadAdmissionResponse(frame, response, capabilities);
            return response;
        }
        if (frame.MessageType == AdapterOriginalDirectoryWire.ChildRequestMessage)
        {
            var request = AdapterOriginalDirectoryWire.ReadChildRequest(frame, capabilities);
            if (backend is not IAdapterOriginalDirectoryBackend effects) throw new NotSupportedException("OriginalDirectoryChildBackendRequired");
            var receipt = await effects.ApplyChildAsync(request, cancellationToken).ConfigureAwait(false);
            var response = frame with
            {
                IsResponse = true,
                MessageType = AdapterOriginalDirectoryWire.ChildResponseMessage,
                Payload = AdapterProtocolJson.ToElement(receipt),
            };
            _ = AdapterOriginalDirectoryWire.ReadChildResponse(frame, response, capabilities);
            return response;
        }
        if (frame.MessageType == AdapterDirectoryRetirementWire.RequestMessage)
        {
            var request = AdapterDirectoryRetirementWire.ReadRequest(frame, capabilities);
            if (backend is not IAdapterOriginalDirectoryBackend effects) throw new NotSupportedException("OriginalDirectoryRetirementBackendRequired");
            var receipt = await effects.RetireAsync(request, cancellationToken).ConfigureAwait(false);
            var response = frame with
            {
                IsResponse = true,
                MessageType = AdapterDirectoryRetirementWire.ResponseMessage,
                Payload = AdapterProtocolJson.ToElement(receipt),
            };
            _ = AdapterDirectoryRetirementWire.ReadResponse(frame, response, capabilities);
            return response;
        }
        throw new NotSupportedException("OriginalDirectoryMessageUnsupported");
    }
}
