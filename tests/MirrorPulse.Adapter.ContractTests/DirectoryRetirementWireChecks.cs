using System.Text.Json;
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.ContractTests;

internal static class DirectoryRetirementWireChecks
{
    internal static void Run()
    {
        string[] capabilities = [AdapterDirectoryEffects.Capability];
        Guid instance = Guid.NewGuid(), admission = Guid.NewGuid(), session = Guid.NewGuid();
        var root = Witness("docs", "", "root-generation", null);
        var directory = Witness("docs", "tree", "tree-generation", root.Generation);
        var first = Child("tree/one.bin", directory.OriginalRevision, "after-one", null);
        var second = Child("tree/two.bin", "after-one", "after-two", AdapterDirectoryEffects.Fingerprint(first));
        var retirement = new AdapterDirectoryRetirementEvidence(1, directory, new string('A', 64),
            [Requirement(first), Requirement(second)], [first, second], "after-two", AdapterDirectoryEffects.Fingerprint(second));
        var operation = new AdapterOperationRequest(Guid.NewGuid(), "docs", "tree", Preconditions: new("after-two"), IsDirectory: true);
        var request = Bind(new(1, "Delete", operation, retirement, [root], [], ""));
        var requestFrame = Frame(AdapterDirectoryRetirementWire.RequestMessage, false, request);
        var receipt = Accepted(request, [new(root, root.OriginalRevision, "root-after-delete", null, "AQID")]);
        var response = Frame(AdapterDirectoryRetirementWire.ResponseMessage, true, receipt) with { RequestId = requestFrame.RequestId };
        var wireRequest = AdapterDirectoryRetirementWire.ReadRequest(RoundTrip(requestFrame), capabilities);
        var wireReceipt = AdapterDirectoryRetirementWire.ReadResponse(RoundTrip(requestFrame), RoundTrip(response), capabilities);
        Expect(wireRequest.RequestFingerprint == request.RequestFingerprint && wireReceipt.AcceptedRevision is null,
            "Wire round trip retains complete original request and a null Delete revision");
        Expect(request.CalculateFingerprint() == (request with
        {
            Retirement = retirement with { RequiredChildren = [Requirement(second), Requirement(first)] }
        }).CalculateFingerprint(), "Manifest set ordering is canonical while accepted effects remain chronological");
        Reject(() => Bind(request with { Retirement = retirement with { AcceptedChildren = [second, first] } }));
        Reject(() => Bind(request with { Retirement = retirement with { AcceptedChildren = [first] } }));
        Reject(() => (request with { Operation = operation with { OperationId = Guid.NewGuid() } }).Validate());
        Reject(() => Bind(request with { Operation = operation with { Preconditions = null } }));
        Reject(() => Bind(request with { Operation = operation with { Preconditions = new(null) } }));
        Reject(() => Bind(request with { Operation = operation with { Preconditions = new("latest-parent-stat") } }));
        Reject(() => Bind(request with { Operation = operation with { IsDirectory = false } }));
        Reject(() => Bind(request with { Operation = operation with { OperationId = first.OperationId } }));
        Reject(() => Bind(request with { Operation = operation with { Path = "later-tree" } }));
        Reject(() => Bind(request with { OperationKind = "Upload" }));
        Reject(() => Bind(request with { OriginalParents = [] }));
        Reject(() => Bind(request with { OriginalParents = [root with { Generation = "later-root" }] }));
        Reject(() => Bind(request with { ParentPredecessors = null! }));
        Reject(() => request.RequireAcceptedReceipt(receipt with { OriginalChildGeneration = "later-directory-generation" }));
        Reject(() => request.RequireAcceptedReceipt(receipt with { RequestFingerprint = new string('B', 64) }));
        Reject(() => request.RequireAcceptedReceipt(receipt with { OperationId = first.OperationId }));
        Reject(() => request.RequireAcceptedReceipt(receipt with { AcceptedRevision = "deletion-is-not-a-parent-revision" }));
        Reject(() => request.RequireAcceptedReceipt(receipt with { Parents = [new(root, "current-stat", "after", new string('C', 64), "AQID")] }));
        Reject(() => request.RequireAcceptedReceipt(receipt with { Parents = [new(root with { WitnessId = Guid.NewGuid() }, root.OriginalRevision, "after", null, "AQID")] }));

        var previous = new AdapterOwnedChildEffectReceipt(1, instance, admission, Guid.NewGuid(), Guid.NewGuid(), "Delete",
            new string('D', 64), new("docs", "peer.bin"), "original-peer-generation", null,
            [new(root, root.OriginalRevision, "root-after-peer", null, "AQID")]);
        var continued = Bind(request with { ParentPredecessors = [previous] });
        Expect(continued.RequestFingerprint != request.RequestFingerprint, "The retained parent predecessor changes mutation authority");
        var continuedReceipt = Accepted(continued,
            [new(root, "root-after-peer", "root-after-directory", AdapterDirectoryEffects.Fingerprint(previous), "AQID")]);
        continued.RequireAcceptedReceipt(continuedReceipt);
        Reject(() => continued.RequireAcceptedReceipt(continuedReceipt with
        { Parents = [new(root, "root-after-peer", "after", new string('E', 64), "AQID")] }));
        Reject(() => continued.RequireAcceptedReceipt(continuedReceipt with
        { Parents = [new(root, root.OriginalRevision, "after", null, "AQID")] }));
        Reject(() => Bind(continued with { ParentPredecessors = [previous, previous] }));
        Reject(() => Bind(continued with { ParentPredecessors = [previous with { AdmissionId = Guid.NewGuid() }] }));
        Reject(() => Bind(continued with { ParentPredecessors = [first] }));

        var destinationParent = Witness("archive", "", "archive-generation", null);
        var moved = Bind(request with
        {
            OperationKind = "Move",
            Operation = operation with { DestinationRootKey = "archive", DestinationPath = "retained-tree" },
            OriginalParents = [root, destinationParent]
        });
        var moveReceipt = Accepted(moved, [new(root, root.OriginalRevision, "root-after", null, "AQID"),
            new(destinationParent, destinationParent.OriginalRevision, "archive-after", null, "AQID")]);
        moved.RequireAcceptedReceipt(moveReceipt);
        Expect(moved.RequestFingerprint == (moved with { OriginalParents = [destinationParent, root] }).CalculateFingerprint(),
            "Source and destination parent order does not change the request");
        Reject(() => moved.RequireAcceptedReceipt(moveReceipt with { Destination = new("archive", "other-tree") }));
        Reject(() => Bind(moved with { Operation = moved.Operation with { DestinationPath = "" } }));
        Reject(() => Bind(moved with { Operation = moved.Operation with { DestinationRootKey = "docs", DestinationPath = "tree/nested" } }));
        Reject(() => Bind(moved with { Operation = moved.Operation with { Preconditions = new("after-two", false) } }));
        Reject(() => Bind(moved with { OriginalParents = [root] }));

        foreach (var bad in new[] { requestFrame with { IsResponse = true }, requestFrame with { RequestId = Guid.Empty },
            requestFrame with { InstanceId = Guid.NewGuid() }, requestFrame with { WorkerSessionId = Guid.Empty },
            requestFrame with { MessageType = "Delete" }, requestFrame with { Payload = AdapterProtocolJson.ToElement<object?>(null) } })
            Reject(() => AdapterDirectoryRetirementWire.ReadRequest(bad, capabilities));
        foreach (var bad in new[] { response with { IsResponse = false }, response with { RequestId = Guid.NewGuid() },
            response with { InstanceId = Guid.NewGuid() }, response with { WorkerSessionId = Guid.NewGuid() },
            response with { MessageType = "MutationComplete" }, response with { Payload = AdapterProtocolJson.ToElement<object?>(null) } })
            Reject(() => AdapterDirectoryRetirementWire.ReadResponse(requestFrame, bad, capabilities));
        Unsupported(() => AdapterDirectoryRetirementWire.ReadRequest(requestFrame, null));
        Unsupported(() => AdapterDirectoryRetirementWire.ReadResponse(requestFrame, response, []));
        Unsupported(() => AdapterDirectoryRetirementWire.ReadRequest(requestFrame with { ProtocolVersion = 1 }, capabilities));
        Unsupported(() => AdapterDirectoryRetirementWire.ReadRequest(requestFrame with { ProtocolVersion = 3 }, capabilities));
        Unsupported(() => AdapterDirectoryRetirementWire.ReadRequest(requestFrame, [AdapterDirectoryEffects.Capability.ToUpperInvariant()]));
        using var oversized = JsonDocument.Parse("{\"padding\":\"" + new string('x', AdapterDirectoryRetirementWire.MaximumPayloadBytes) + "\"}");
        Reject(() => AdapterDirectoryRetirementWire.ReadRequest(requestFrame with { Payload = oversized.RootElement }, capabilities));
        Expect(!AdapterHandshake.V2Capabilities.Contains(AdapterDirectoryEffects.Capability), "Wire helpers do not advertise backend qualification");
        Console.WriteLine("Directory retirement wire checks passed: original child ledger, exact parent predecessors, conditional mutation and correlated response; no backend capability is enabled.");

        AdapterDirectoryGenerationWitness Witness(string key, string path, string generation, string? parent) =>
            new(1, instance, admission, Guid.NewGuid(), new(key, path), generation, generation + "-revision", parent, new string('A', 64), "AQID");
        AdapterOwnedChildEffectReceipt Child(string path, string before, string after, string? prior) =>
            new(1, instance, admission, Guid.NewGuid(), Guid.NewGuid(), "Delete", new string('A', 64), new("docs", path), path + "-generation",
                null, [new(directory, before, after, prior, "AQID")]);
        AdapterControlFrame Frame(string name, bool responseFlag, object payload) =>
            new(2, name, Guid.NewGuid(), instance, session, responseFlag, AdapterProtocolJson.ToElement(payload));
    }

    private static AdapterDirectoryChildRequirement Requirement(AdapterOwnedChildEffectReceipt child) =>
        new(child.OperationId, child.RequestFingerprint, child.Child, child.OriginalChildGeneration);
    private static AdapterDirectoryRetirementRequest Bind(AdapterDirectoryRetirementRequest request) =>
        request with { RequestFingerprint = request.CalculateFingerprint() };
    private static AdapterControlFrame RoundTrip(AdapterControlFrame frame) =>
        AdapterProtocolJson.Decode<AdapterControlFrame>(AdapterProtocolJson.Encode(frame));
    private static AdapterOwnedChildEffectReceipt Accepted(AdapterDirectoryRetirementRequest request, IReadOnlyList<AdapterOwnedParentTransition> parents)
    {
        var original = request.Retirement.OriginalDirectory;
        return new(1, original.InstanceId, original.AdmissionId, Guid.NewGuid(), request.Operation.OperationId, request.OperationKind,
            request.RequestFingerprint, original.Directory, original.Generation, request.OperationKind == "Delete" ? null : "accepted-move", parents,
            request.OperationKind == "Move" ? new(request.Operation.DestinationRootKey!, request.Operation.DestinationPath!) : null);
    }
    private static void Expect(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Expected an invalid directory retirement wire message to be refused.");
    }
    private static void Unsupported(Action action)
    {
        try { action(); } catch (NotSupportedException) { return; }
        throw new InvalidOperationException("Expected unnegotiated directory retirement wire support to be refused.");
    }
}
