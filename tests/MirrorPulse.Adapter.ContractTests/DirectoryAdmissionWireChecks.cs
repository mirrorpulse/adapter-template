using System.Text.Json;
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.ContractTests;

internal static class DirectoryAdmissionWireChecks
{
    private static readonly string[] Capabilities = [AdapterDirectoryEffects.Capability, AdapterOriginalDirectoryWire.Capability,
        AdapterOriginalDirectoryWire.ChildCapability];

    internal static async Task RunAsync()
    {
        var fixture = new Fixture();
        var requestFrame = fixture.Frame(AdapterOriginalDirectoryWire.AdmissionRequestMessage, fixture.Request);
        var backend = new Backend(fixture);
        var response = await AdapterOriginalDirectoryDispatcher.DispatchAsync(RoundTrip(requestFrame), Capabilities, backend);
        var admitted = AdapterOriginalDirectoryWire.ReadAdmissionResponse(requestFrame, RoundTrip(response), Capabilities);
        Expect(admitted.AdmissionFingerprint == fixture.Admission.AdmissionFingerprint && backend.Admissions == 1,
            "The dispatcher returns the correlated complete admission");
        var child = fixture.Child();
        child.RequireAdmission(fixture.Request, admitted);
        var childFrame = fixture.Frame(AdapterOriginalDirectoryWire.ChildRequestMessage, child);
        var applied = await AdapterOriginalDirectoryDispatcher.DispatchAsync(RoundTrip(childFrame), Capabilities, backend);
        var accepted = AdapterOriginalDirectoryWire.ReadChildResponse(childFrame, RoundTrip(applied), Capabilities);
        Expect(accepted.AcceptedRevision is null && backend.Effects == 1 && accepted.OperationId == fixture.Leaf.OperationId,
            "A typed child Delete retains its null revision and exact operation");
        Expect((await AdapterOriginalDirectoryDispatcher.DispatchAsync(childFrame, Capabilities, backend)).Payload.GetRawText() ==
            applied.Payload.GetRawText(), "A backend can return its original immutable child receipt");
        var retirement = fixture.Retirement(child, accepted);
        var finalFrame = fixture.Frame(AdapterDirectoryRetirementWire.RequestMessage, retirement);
        var retired = await AdapterOriginalDirectoryDispatcher.DispatchAsync(finalFrame, Capabilities, backend);
        _ = AdapterDirectoryRetirementWire.ReadResponse(finalFrame, retired, Capabilities);
        Expect(backend.Retirements == 1, "The same dispatcher returns a typed final directory retirement");
        CheckAdmissionRefusals(fixture, requestFrame, response);
        CheckChildRefusals(fixture, child, childFrame, applied);

        var admissionOnly = new AdmissionOnlyBackend(fixture.Admission);
        var observed = await AdapterOriginalDirectoryDispatcher.DispatchAsync(requestFrame, [AdapterOriginalDirectoryWire.Capability], admissionOnly);
        _ = AdapterOriginalDirectoryWire.ReadAdmissionResponse(requestFrame, observed, [AdapterOriginalDirectoryWire.Capability]);
        await UnsupportedAsync(() => AdapterOriginalDirectoryDispatcher.DispatchAsync(childFrame, Capabilities, admissionOnly));
        await UnsupportedAsync(() => AdapterOriginalDirectoryDispatcher.DispatchAsync(finalFrame, Capabilities, admissionOnly));

        int calls = backend.Admissions + backend.Effects + backend.Retirements;
        await UnsupportedAsync(() => AdapterOriginalDirectoryDispatcher.DispatchAsync(requestFrame, [AdapterDirectoryEffects.Capability], backend));
        await UnsupportedAsync(() => AdapterOriginalDirectoryDispatcher.DispatchAsync(childFrame,
            [AdapterDirectoryEffects.Capability, AdapterOriginalDirectoryWire.Capability], backend));
        await UnsupportedAsync(() => AdapterOriginalDirectoryDispatcher.DispatchAsync(childFrame,
            [AdapterOriginalDirectoryWire.Capability, AdapterOriginalDirectoryWire.ChildCapability], backend));
        await UnsupportedAsync(() => AdapterOriginalDirectoryDispatcher.DispatchAsync(requestFrame with { ProtocolVersion = 1 }, Capabilities, backend));
        await RejectAsync(() => AdapterOriginalDirectoryDispatcher.DispatchAsync(requestFrame with
        {
            Payload = AdapterProtocolJson.ToElement(fixture.Request with { AdmissionId = Guid.NewGuid() }),
        }, Capabilities, backend));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        try
        {
            await AdapterOriginalDirectoryDispatcher.DispatchAsync(childFrame, Capabilities, backend, cancellation.Token);
            throw new InvalidOperationException("A canceled dispatcher called its backend.");
        }
        catch (OperationCanceledException) { }
        Expect(calls == backend.Admissions + backend.Effects + backend.Retirements, "Rejected and canceled requests never enter the backend");
        backend.CorruptReceipt = true;
        await RejectAsync(() => AdapterOriginalDirectoryDispatcher.DispatchAsync(childFrame, Capabilities, backend));
        await CheckMoveAsync();
        CheckOriginalPredecessors();
        Console.WriteLine("Original directory admission and child dispatch boundary checks passed.");
    }

    private static void CheckOriginalPredecessors()
    {
        var fixture = new Fixture(secondLeaf: true);
        var first = fixture.Receipt(fixture.Child());
        var second = fixture.Child(1) with { ParentPredecessors = [new(fixture.Tree, first)] };
        second = second with { RequestFingerprint = second.CalculateFingerprint() };
        second.RequireAdmission(fixture.Request, fixture.Admission);
        second.RequireAcceptedReceipt(fixture.Receipt(second));
        var foreign = second with
        {
            ParentPredecessors = [new(fixture.Tree, first with { Child = new("source", "tree/foreign") })],
        };
        foreign = foreign with { RequestFingerprint = foreign.CalculateFingerprint() };
        Reject(() => foreign.RequireAdmission(fixture.Request, fixture.Admission));
        var future = second with
        {
            ParentPredecessors = [new(fixture.Tree, first with { OperationId = fixture.Directory.OperationId })],
        };
        future = future with { RequestFingerprint = future.CalculateFingerprint() };
        Reject(() => future.RequireAdmission(fixture.Request, fixture.Admission));
    }

    private static async Task CheckMoveAsync()
    {
        var fixture = new Fixture(moving: true);
        var backend = new Backend(fixture);
        var frame = fixture.Frame(AdapterOriginalDirectoryWire.AdmissionRequestMessage, fixture.Request);
        var response = await AdapterOriginalDirectoryDispatcher.DispatchAsync(frame, Capabilities, backend);
        _ = AdapterOriginalDirectoryWire.ReadAdmissionResponse(frame, response, Capabilities);
        var child = fixture.Child();
        child.RequireAdmission(fixture.Request, fixture.Admission);
        var childFrame = fixture.Frame(AdapterOriginalDirectoryWire.ChildRequestMessage, child);
        var applied = await AdapterOriginalDirectoryDispatcher.DispatchAsync(childFrame, Capabilities, backend);
        var receipt = AdapterOriginalDirectoryWire.ReadChildResponse(childFrame, applied, Capabilities);
        Expect(receipt.Parents.Count == 2 && receipt.AcceptedRevision == "leaf-moved-revision" &&
            receipt.Destination == new AdapterFileAddress("target", "leaf"), "A moved child retains both parents and its destination");
        var retirement = fixture.Retirement(child, receipt);
        var finalFrame = fixture.Frame(AdapterDirectoryRetirementWire.RequestMessage, retirement);
        var finalResponse = await AdapterOriginalDirectoryDispatcher.DispatchAsync(finalFrame, Capabilities, backend);
        var final = AdapterDirectoryRetirementWire.ReadResponse(finalFrame, finalResponse, Capabilities);
        Expect(final.Parents.Count == 2 && final.Destination == new AdapterFileAddress("target", "moved-tree"),
            "The final Move extends the destination parent predecessor");
        var reordered = fixture.Admission with
        {
            Operations = fixture.Admission.Operations.Select(operation => operation with
            {
                OriginalParents = operation.OriginalParents.Reverse().ToArray(),
            }).ToArray(),
        };
        Expect(reordered.CalculateFingerprint() == fixture.Admission.AdmissionFingerprint, "Parent set ordering is canonical");
        reordered.RequireRequest(fixture.Request);
        Reject(() => (fixture.Request with
        {
            Operations = [fixture.Request.Operations[0] with
            {
                Operation = fixture.Leaf with { DestinationRootKey = "source", DestinationPath = "tree/new" },
            }, fixture.Request.Operations[1]],
        }).CalculateFingerprint());
    }

    private static void CheckAdmissionRefusals(Fixture fixture, AdapterControlFrame request, AdapterControlFrame response)
    {
        var original = fixture.Request;
        Reject(() => (original with { AdmissionId = Guid.NewGuid() }).Validate());
        Reject(() => (original with { Operations = [original.Operations[1], original.Operations[0]] }).CalculateFingerprint());
        Reject(() => (original with { Operations = [original.Operations[0], original.Operations[0], original.Operations[1]] }).CalculateFingerprint());
        Reject(() => (original with
        {
            Operations = [original.Operations[0] with { Operation = fixture.Leaf with { Path = "other/leaf" } }, original.Operations[1]],
        }).CalculateFingerprint());
        Reject(() => (original with
        {
            Operations = [original.Operations[0], original.Operations[1] with { Operation = fixture.Directory with { Preconditions = null } }],
        }).CalculateFingerprint());
        Reject(() => (fixture.Admission with { Operations = [fixture.Admission.Operations[1]] }).RequireRequest(original));
        var changed = fixture.Admission with
        {
            Operations = [fixture.Admission.Operations[0] with { OriginalParents = [fixture.Tree with { Generation = "successor" }] },
                fixture.Admission.Operations[1]],
        };
        changed = changed with { AdmissionFingerprint = changed.CalculateFingerprint() };
        Reject(() => changed.RequireRequest(original));
        var reordered = fixture.Admission with { Operations = fixture.Admission.Operations.Reverse().ToArray() };
        reordered = reordered with { AdmissionFingerprint = reordered.CalculateFingerprint() };
        Reject(() => reordered.RequireRequest(original));
        Reject(() => AdapterOriginalDirectoryWire.ReadAdmissionResponse(request, response with { WorkerSessionId = Guid.NewGuid() }, Capabilities));
        Reject(() => AdapterOriginalDirectoryWire.ReadAdmissionResponse(request, response with { RequestId = Guid.NewGuid() }, Capabilities));
        Reject(() => AdapterOriginalDirectoryWire.ReadAdmissionRequest(request with { InstanceId = Guid.NewGuid() }, Capabilities));
        Reject(() => AdapterOriginalDirectoryWire.ReadAdmissionResponse(request, response with { IsResponse = false }, Capabilities));
        using var tooLarge = JsonDocument.Parse("{\"padding\":\"" + new string('x', AdapterDirectoryRetirementWire.MaximumPayloadBytes) + "\"}");
        Reject(() => AdapterOriginalDirectoryWire.ReadAdmissionRequest(request with { Payload = tooLarge.RootElement }, Capabilities));
    }

    private static void CheckChildRefusals(Fixture fixture, AdapterOriginalChildEffectRequest child,
        AdapterControlFrame request, AdapterControlFrame response)
    {
        Reject(() => (child with { OriginalGeneration = "successor" }).Validate());
        var changed = child with { OriginalGeneration = "successor" };
        changed = changed with { RequestFingerprint = changed.CalculateFingerprint() };
        Reject(() => changed.RequireAdmission(fixture.Request, fixture.Admission));
        var later = child with { Operation = child.Operation with { Preconditions = new("fresh-stat") } };
        later = later with { RequestFingerprint = later.CalculateFingerprint() };
        Reject(() => later.RequireAdmission(fixture.Request, fixture.Admission));
        Reject(() => (child with { Operation = fixture.Directory }).CalculateFingerprint());
        Reject(() => (child with { ParentPredecessors = null! }).CalculateFingerprint());
        Reject(() => AdapterOriginalDirectoryWire.ReadChildResponse(request, response with { MessageType = "MutationComplete" }, Capabilities));
        Reject(() => AdapterOriginalDirectoryWire.ReadChildResponse(request, response with { InstanceId = Guid.NewGuid() }, Capabilities));
        Reject(() => child.RequireAcceptedReceipt(fixture.Receipt(child) with { AcceptedRevision = "invented-delete-revision" }));
        Reject(() => child.RequireAcceptedReceipt(fixture.Receipt(child) with { OriginalChildGeneration = "successor" }));

        var prior = fixture.Receipt(child) with { OperationId = Guid.NewGuid() };
        var next = child with
        {
            Operation = child.Operation with { OperationId = Guid.NewGuid(), Path = "tree/second" },
            ParentPredecessors = [new(fixture.Tree, prior)],
        };
        next = next with { RequestFingerprint = next.CalculateFingerprint() };
        var receipt = fixture.Receipt(next) with
        {
            Child = new("source", "tree/second"),
            Parents = [new(fixture.Tree, "tree-after-leaf", "tree-after-second", AdapterDirectoryEffects.Fingerprint(prior), "AQID")],
        };
        next.RequireAcceptedReceipt(receipt);
        Reject(() => next.RequireAdmission(fixture.Request, fixture.Admission));
        Reject(() => next.RequireAcceptedReceipt(receipt with
        {
            Parents = [new(fixture.Tree, fixture.Tree.OriginalRevision, "tree-after-second", null, "AQID")],
        }));
        Unsupported(() => AdapterOriginalDirectoryWire.ReadChildRequest(request, [AdapterDirectoryEffects.Capability]));
    }

    internal sealed class Fixture
    {
        internal Guid Session { get; } = Guid.NewGuid();
        internal AdapterOperationRequest Leaf { get; }
        internal AdapterOperationRequest Directory { get; }
        internal AdapterDirectoryAdmissionRequest Request { get; }
        internal AdapterDirectoryGenerationWitness Root { get; }
        internal AdapterDirectoryGenerationWitness Tree { get; }
        internal AdapterDirectoryAdmission Admission { get; }

        internal Fixture(bool moving = false, bool secondLeaf = false)
        {
            Leaf = new(Guid.NewGuid(), "source", "tree/leaf", moving ? "target" : null, moving ? "leaf" : null, new("leaf-revision"));
            Directory = new(Guid.NewGuid(), "source", "tree", moving ? "target" : null, moving ? "moved-tree" : null,
                new("tree-revision"), IsDirectory: true);
            string kind = moving ? "Move" : "Delete";
            var operations = new List<AdapterOriginalDirectoryOperation> { new(kind, Leaf) };
            if (secondLeaf)
                operations.Add(new(kind, Leaf with
                {
                    OperationId = Guid.NewGuid(),
                    Path = "tree/second",
                    DestinationPath = moving ? "second" : null,
                }));
            operations.Add(new(kind, Directory));
            var request = new AdapterDirectoryAdmissionRequest(1, Guid.NewGuid(), Guid.NewGuid(), operations, "");
            Request = request with { RequestFingerprint = request.CalculateFingerprint() };
            Root = Witness("", "root-generation", "root-revision", null);
            Tree = Witness("tree", "tree-generation", "tree-revision", Root.Generation);
            var target = Root with { WitnessId = Guid.NewGuid(), Directory = new("target", ""), Generation = "target-generation" };
            var entries = operations.Select(operation => operation.Operation.IsDirectory
                ? new AdapterAdmittedDirectoryOperation(Directory.OperationId, Tree.Generation, Tree, moving ? [Root, target] : [Root])
                : new AdapterAdmittedDirectoryOperation(operation.Operation.OperationId, "leaf-generation", null, moving ? [Tree, target] : [Tree])).ToArray();
            var admission = new AdapterDirectoryAdmission(1, Request.InstanceId, Request.AdmissionId, Request.RequestFingerprint, entries, "");
            Admission = admission with { AdmissionFingerprint = admission.CalculateFingerprint() };
        }

        internal AdapterOriginalChildEffectRequest Child(int ordinal = 0)
        {
            var child = new AdapterOriginalChildEffectRequest(1, Request.InstanceId, Request.AdmissionId, Admission.AdmissionFingerprint,
                new string('B', 64), Request.Operations[ordinal].OperationKind, Request.Operations[ordinal].Operation,
                "leaf-generation", Admission.Operations[ordinal].OriginalParents, [], "");
            return child with { RequestFingerprint = child.CalculateFingerprint() };
        }

        internal AdapterOwnedChildEffectReceipt Receipt(AdapterOriginalChildEffectRequest child) =>
            new(1, Request.InstanceId, Request.AdmissionId, Guid.NewGuid(), child.Operation.OperationId, child.OperationKind,
                child.RequestFingerprint, new("source", child.Operation.Path), child.OriginalGeneration,
                child.OperationKind == "Move" ? "leaf-moved-revision" : null,
                child.OriginalParents.Select(parent =>
                {
                    var previous = child.ParentPredecessors.SingleOrDefault(value => value.Parent == parent)?.Receipt;
                    return new AdapterOwnedParentTransition(parent,
                        previous?.Parents.Single(value => value.OriginalParent == parent).AfterRevision ?? parent.OriginalRevision,
                        "tree-after-leaf", previous is null ? null : AdapterDirectoryEffects.Fingerprint(previous), "AQID");
                }).ToArray(),
                child.OperationKind == "Move" ? new(child.Operation.DestinationRootKey!, child.Operation.DestinationPath!) : null);

        internal AdapterDirectoryRetirementRequest Retirement(AdapterOriginalChildEffectRequest child, AdapterOwnedChildEffectReceipt receipt)
        {
            var parents = Admission.Operations[^1].OriginalParents;
            var predecessors = parents.Any(parent => receipt.Parents.Any(transition => transition.OriginalParent == parent))
                ? new[] { receipt } : [];
            var retirement = new AdapterDirectoryRetirementRequest(1, Request.Operations[^1].OperationKind,
                Directory with { Preconditions = new("tree-after-leaf") },
                new(1, Tree, child.ManifestFingerprint,
                    [new(receipt.OperationId, receipt.RequestFingerprint, receipt.Child, receipt.OriginalChildGeneration)],
                    [receipt], "tree-after-leaf", AdapterDirectoryEffects.Fingerprint(receipt)), parents, predecessors, "");
            return retirement with { RequestFingerprint = retirement.CalculateFingerprint() };
        }

        internal AdapterControlFrame Frame(string message, object value) =>
            new(2, message, Guid.NewGuid(), Request.InstanceId, Session, false, AdapterProtocolJson.ToElement(value));

        private AdapterDirectoryGenerationWitness Witness(string path, string generation, string revision, string? parent) =>
            new(1, Request.InstanceId, Request.AdmissionId, Guid.NewGuid(), new("source", path), generation, revision, parent,
                Request.RequestFingerprint, "AQID");
    }

    private sealed class AdmissionOnlyBackend(AdapterDirectoryAdmission admission) : IAdapterOriginalDirectoryAdmissionBackend
    {
        public ValueTask<AdapterDirectoryAdmission> AdmitAsync(AdapterDirectoryAdmissionRequest request, CancellationToken cancellationToken)
        {
            admission.RequireRequest(request);
            return ValueTask.FromResult(admission);
        }
    }

    private sealed class Backend(Fixture fixture) : IAdapterOriginalDirectoryBackend
    {
        private AdapterOwnedChildEffectReceipt? _accepted;
        internal int Admissions { get; private set; }
        internal int Effects { get; private set; }
        internal int Retirements { get; private set; }
        internal bool CorruptReceipt { get; set; }

        public ValueTask<AdapterDirectoryAdmission> AdmitAsync(AdapterDirectoryAdmissionRequest request, CancellationToken cancellationToken)
        {
            Admissions++;
            fixture.Admission.RequireRequest(request);
            return ValueTask.FromResult(fixture.Admission);
        }

        public ValueTask<AdapterOwnedChildEffectReceipt> ApplyChildAsync(AdapterOriginalChildEffectRequest request, CancellationToken cancellationToken)
        {
            Effects++;
            request.RequireAdmission(fixture.Request, fixture.Admission);
            _accepted ??= fixture.Receipt(request);
            return ValueTask.FromResult(CorruptReceipt ? _accepted with { OriginalChildGeneration = "successor" } : _accepted);
        }

        public ValueTask<AdapterOwnedChildEffectReceipt> RetireAsync(AdapterDirectoryRetirementRequest request, CancellationToken cancellationToken)
        {
            Retirements++;
            var receipt = new AdapterOwnedChildEffectReceipt(1, fixture.Request.InstanceId, fixture.Request.AdmissionId, Guid.NewGuid(),
                fixture.Directory.OperationId, request.OperationKind, request.RequestFingerprint, fixture.Tree.Directory, fixture.Tree.Generation,
                request.OperationKind == "Move" ? "directory-moved-revision" : null,
                request.OriginalParents.Select(parent =>
                {
                    var previous = request.ParentPredecessors.SingleOrDefault(effect => effect.Parents.Any(value => value.OriginalParent == parent));
                    return new AdapterOwnedParentTransition(parent,
                        previous?.Parents.Single(value => value.OriginalParent == parent).AfterRevision ?? parent.OriginalRevision,
                        "root-after-tree", previous is null ? null : AdapterDirectoryEffects.Fingerprint(previous), "AQID");
                }).ToArray(), request.OperationKind == "Move" ? new(request.Operation.DestinationRootKey!, request.Operation.DestinationPath!) : null);
            return ValueTask.FromResult(receipt);
        }
    }

    private static AdapterControlFrame RoundTrip(AdapterControlFrame frame) => AdapterProtocolJson.Decode<AdapterControlFrame>(AdapterProtocolJson.Encode(frame));
    private static void Expect(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Expected original directory evidence refusal.");
    }
    private static void Unsupported(Action action)
    {
        try { action(); } catch (NotSupportedException) { return; }
        throw new InvalidOperationException("Expected missing original directory capability refusal.");
    }
    private static async Task RejectAsync(Func<ValueTask<AdapterControlFrame>> action)
    {
        try { await action(); } catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Expected original directory dispatch refusal.");
    }
    private static async Task UnsupportedAsync(Func<ValueTask<AdapterControlFrame>> action)
    {
        try { await action(); } catch (NotSupportedException) { return; }
        throw new InvalidOperationException("Expected original directory dispatch capability refusal.");
    }
}
