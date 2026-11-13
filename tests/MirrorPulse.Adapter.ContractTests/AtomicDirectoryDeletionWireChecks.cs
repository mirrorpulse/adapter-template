using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.ContractTests;

internal static class AtomicDirectoryDeletionWireChecks
{
    private static readonly string[] Capabilities = [AdapterOriginalDirectoryWire.Capability,
        AdapterAtomicDirectoryDeletionWire.Capability];

    internal static async Task RunAsync()
    {
        var fixture = new DirectoryAdmissionWireChecks.Fixture(secondLeaf: true);
        var request = Request(fixture);
        var frame = fixture.Frame(AdapterAtomicDirectoryDeletionWire.RequestMessage, request);
        var backend = new ProtocolBackend();
        Expect(!AdapterHandshake.V2Capabilities.Contains(AdapterOriginalDirectoryWire.Capability) &&
            !AdapterHandshake.V2Capabilities.Contains(AdapterOriginalDirectoryWire.ChildCapability) &&
            !AdapterHandshake.V2Capabilities.Contains(AdapterAtomicDirectoryDeletionWire.Capability),
            "Default protocol-v2 handshakes do not enable directory admission or effects");
        var response = await AdapterAtomicDirectoryDeletionDispatcher.DispatchAsync(RoundTrip(frame), Capabilities, backend);
        var accepted = AdapterAtomicDirectoryDeletionWire.ReadResponse(frame, RoundTrip(response), Capabilities);
        Expect(accepted.DeletedOriginals.Count == 3 && backend.Commits == 1, "The reply retains every authorized original");
        Expect(accepted.DeletedOriginals[^1].Source == fixture.Tree.Directory, "The subtree directory follows its original children");
        var replayFrame = frame with { RequestId = Guid.NewGuid(), WorkerSessionId = Guid.NewGuid() };
        var replay = await AdapterAtomicDirectoryDeletionDispatcher.DispatchAsync(replayFrame, Capabilities, backend);
        var replayReceipt = AdapterAtomicDirectoryDeletionWire.ReadResponse(replayFrame, replay, Capabilities);
        Expect(replayReceipt.Fingerprint() == accepted.Fingerprint() && backend.Commits == 1,
            "A historical receipt uses the new transport correlation and the original effect identity");

        CheckRequestRefusals(fixture, request);
        CheckReceiptRefusals(request, accepted);
        CheckResponseRefusals(frame, response);
        await CheckDispatchRefusals(frame, backend);
        var rebound = request with { ManifestFingerprint = new string('C', 64) };
        rebound = rebound with { RequestFingerprint = rebound.CalculateFingerprint() };
        await RejectAsync(() => AdapterAtomicDirectoryDeletionDispatcher.DispatchAsync(frame with
        {
            Payload = AdapterProtocolJson.ToElement(rebound),
        }, Capabilities, backend));
        Expect(backend.Commits == 1, "A backend must refuse a changed immutable authorization");

        using var cancellation = new CancellationTokenSource();
        var committedBackend = new ProtocolBackend { AfterCommit = cancellation.Cancel };
        var committed = await AdapterAtomicDirectoryDeletionDispatcher.DispatchAsync(frame, Capabilities, committedBackend, cancellation.Token);
        _ = AdapterAtomicDirectoryDeletionWire.ReadResponse(frame, committed, Capabilities);
        Expect(cancellation.IsCancellationRequested && committedBackend.Commits == 1,
            "Cancellation after backend commit does not discard the accepted reply");
        var badBackend = new ProtocolBackend { CorruptReceipt = true };
        await RejectAsync(() => AdapterAtomicDirectoryDeletionDispatcher.DispatchAsync(frame, Capabilities, badBackend));
        Console.WriteLine("Atomic original-directory deletion protocol checks passed (synthetic backend, no native durability claim).");
    }

    private static void CheckRequestRefusals(DirectoryAdmissionWireChecks.Fixture fixture, AdapterAtomicDirectoryDeletionRequest request)
    {
        Reject(() => (request with { ManifestFingerprint = new string('C', 64) }).Validate());
        Reject(() => (request with { ManifestFingerprint = "", RequestFingerprint = "" }).CalculateFingerprint());
        Reject(() => (request with { OriginalRequest = null! }).CalculateFingerprint());
        Reject(() => (request with { Admission = null! }).CalculateFingerprint());
        var changedOriginal = fixture.Request with
        {
            Operations = [fixture.Request.Operations[0] with
            {
                Operation = fixture.Leaf with { Preconditions = new("a-later-revision") },
            }, .. fixture.Request.Operations.Skip(1)],
        };
        changedOriginal = changedOriginal with { RequestFingerprint = changedOriginal.CalculateFingerprint() };
        Reject(() => (request with { OriginalRequest = changedOriginal }).CalculateFingerprint());
        var changedAdmission = fixture.Admission with
        {
            Operations = [fixture.Admission.Operations[0] with { OriginalGeneration = "successor" }, .. fixture.Admission.Operations.Skip(1)],
        };
        changedAdmission = changedAdmission with { AdmissionFingerprint = changedAdmission.CalculateFingerprint() };
        Reject(() => (request with { Admission = changedAdmission }).Validate());
        var move = new DirectoryAdmissionWireChecks.Fixture(moving: true);
        Unsupported(() => Request(move));
    }

    private static void CheckReceiptRefusals(AdapterAtomicDirectoryDeletionRequest request, AdapterAtomicDirectoryDeletionReceipt receipt)
    {
        Reject(() => (receipt with { InstanceId = Guid.NewGuid() }).RequireRequest(request));
        Reject(() => (receipt with { AdmissionId = Guid.NewGuid() }).RequireRequest(request));
        Reject(() => (receipt with { RequestFingerprint = new string('D', 64) }).RequireRequest(request));
        Reject(() => (receipt with { AdmissionFingerprint = new string('D', 64) }).RequireRequest(request));
        Reject(() => (receipt with { ManifestFingerprint = new string('D', 64) }).RequireRequest(request));
        Reject(() => (receipt with { ReceiptId = Guid.Empty }).RequireRequest(request));
        Reject(() => (receipt with { BackendEvidence = "not-base64" }).RequireRequest(request));
        Reject(() => (receipt with { DeletedOriginals = receipt.DeletedOriginals.Skip(1).ToArray() }).RequireRequest(request));
        Reject(() => (receipt with { DeletedOriginals = receipt.DeletedOriginals.Reverse().ToArray() }).RequireRequest(request));
        Reject(() => (receipt with { DeletedOriginals = [receipt.DeletedOriginals[0], receipt.DeletedOriginals[0], receipt.DeletedOriginals[2]] })
            .RequireRequest(request));
        Reject(() => (receipt with
        {
            DeletedOriginals = [receipt.DeletedOriginals[0] with { OriginalGeneration = "successor" },
            .. receipt.DeletedOriginals.Skip(1)]
        }).RequireRequest(request));
        Reject(() => (receipt with
        {
            DeletedOriginals = [receipt.DeletedOriginals[0] with { Source = new("source", "tree/occupant") },
            .. receipt.DeletedOriginals.Skip(1)]
        }).RequireRequest(request));
        Reject(() => (receipt with
        {
            DeletedOriginals = [receipt.DeletedOriginals[0] with { OperationId = Guid.NewGuid() },
            .. receipt.DeletedOriginals.Skip(1)]
        }).RequireRequest(request));
    }

    private static void CheckResponseRefusals(AdapterControlFrame request, AdapterControlFrame response)
    {
        foreach (var changed in new[]
        {
            response with { RequestId = Guid.NewGuid() }, response with { InstanceId = Guid.NewGuid() },
            response with { WorkerSessionId = Guid.NewGuid() }, response with { IsResponse = false },
            response with { MessageType = "MutationComplete" },
        }) Reject(() => AdapterAtomicDirectoryDeletionWire.ReadResponse(request, changed, Capabilities));
        var oversized = response with
        {
            Payload = AdapterProtocolJson.ToElement(new { padding = new string('x', AdapterDirectoryRetirementWire.MaximumPayloadBytes) }),
        };
        Reject(() => AdapterAtomicDirectoryDeletionWire.ReadResponse(request, oversized, Capabilities));
    }

    private static async Task CheckDispatchRefusals(AdapterControlFrame frame, ProtocolBackend backend)
    {
        int calls = backend.Calls;
        foreach (string omitted in Capabilities)
            await UnsupportedAsync(() => AdapterAtomicDirectoryDeletionDispatcher.DispatchAsync(frame,
                Capabilities.Where(value => value != omitted), backend));
        await UnsupportedAsync(() => AdapterAtomicDirectoryDeletionDispatcher.DispatchAsync(frame with { ProtocolVersion = 1 }, Capabilities, backend));
        foreach (var changed in new[]
        {
            frame with { IsResponse = true }, frame with { RequestId = Guid.Empty }, frame with { WorkerSessionId = Guid.Empty },
            frame with { InstanceId = Guid.NewGuid() }, frame with { MessageType = "Delete" },
            frame with { Payload = AdapterProtocolJson.ToElement(new { padding = new string('x', AdapterDirectoryRetirementWire.MaximumPayloadBytes) }) },
        }) await RejectAsync(() => AdapterAtomicDirectoryDeletionDispatcher.DispatchAsync(changed, Capabilities, backend));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await AdapterAtomicDirectoryDeletionDispatcher.DispatchAsync(frame, Capabilities, backend, cancellation.Token);
            throw new InvalidOperationException("A canceled request entered the backend.");
        }
        catch (OperationCanceledException) { }
        Expect(calls == backend.Calls, "Invalid, nonnegotiated and canceled requests never enter the backend");
    }

    private static AdapterAtomicDirectoryDeletionRequest Request(DirectoryAdmissionWireChecks.Fixture fixture)
    {
        var request = new AdapterAtomicDirectoryDeletionRequest(1, fixture.Request, fixture.Admission, new string('B', 64), "");
        return request with { RequestFingerprint = request.CalculateFingerprint() };
    }

    // Protocol-only test double. This deliberately makes no filesystem, native atomicity or restart claim.
    private sealed class ProtocolBackend : IAdapterAtomicDirectoryDeletionBackend
    {
        private AdapterAtomicDirectoryDeletionReceipt? _receipt;
        internal int Calls { get; private set; }
        internal int Commits { get; private set; }
        internal bool CorruptReceipt { get; init; }
        internal Action? AfterCommit { get; init; }

        public ValueTask<AdapterAtomicDirectoryDeletionReceipt> DeleteAtomicallyAsync(AdapterAtomicDirectoryDeletionRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (_receipt is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _receipt = new(1, request.OriginalRequest.InstanceId, request.OriginalRequest.AdmissionId, Guid.NewGuid(),
                    request.RequestFingerprint, request.Admission.AdmissionFingerprint, request.ManifestFingerprint,
                    request.OriginalRequest.Operations.Select((step, ordinal) => new AdapterAtomicDirectoryDeletedOriginal(step.Operation.OperationId,
                        new(step.Operation.RootKey, step.Operation.Path), request.Admission.Operations[ordinal].OriginalGeneration)).ToArray(), "AQID");
                Commits++;
                AfterCommit?.Invoke();
            }
            _receipt.RequireRequest(request);
            return ValueTask.FromResult(CorruptReceipt ? _receipt with { DeletedOriginals = _receipt.DeletedOriginals.Skip(1).ToArray() } : _receipt);
        }
    }

    private static AdapterControlFrame RoundTrip(AdapterControlFrame frame) => AdapterProtocolJson.Decode<AdapterControlFrame>(AdapterProtocolJson.Encode(frame));
    private static void Expect(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Expected atomic directory deletion refusal.");
    }
    private static void Unsupported(Action action)
    {
        try { action(); } catch (NotSupportedException) { return; }
        throw new InvalidOperationException("Expected atomic directory deletion capability refusal.");
    }
    private static async Task RejectAsync(Func<ValueTask<AdapterControlFrame>> action)
    {
        try { await action(); } catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Expected atomic directory deletion dispatch refusal.");
    }
    private static async Task UnsupportedAsync(Func<ValueTask<AdapterControlFrame>> action)
    {
        try { await action(); } catch (NotSupportedException) { return; }
        throw new InvalidOperationException("Expected atomic directory deletion dispatch capability refusal.");
    }
}
