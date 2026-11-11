using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.ContractTests;

internal static class DirectoryEffectContractChecks
{
    internal static void Run()
    {
        Guid instance = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid admission = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var witness = new AdapterDirectoryGenerationWitness(1, instance, admission,
            Guid.Parse("33333333-3333-3333-3333-333333333333"), new("docs", "tree"),
            "original-directory-generation", "original-parent-revision", "original-root-generation", new string('A', 64), "AQID");
        witness.Validate();
        AdapterDirectoryEffects.RequireNegotiated(2, [AdapterDirectoryEffects.Capability]);
        Expect(!AdapterHandshake.V2Capabilities.Contains(AdapterDirectoryEffects.Capability), "V2 must not silently advertise directory evidence");
        foreach (int protocol in new[] { 1, 3 })
            Unsupported(() => AdapterDirectoryEffects.RequireNegotiated(protocol, [AdapterDirectoryEffects.Capability]));
        Unsupported(() => AdapterDirectoryEffects.RequireNegotiated(2, []));
        Unsupported(() => AdapterDirectoryEffects.RequireNegotiated(2, [AdapterDirectoryEffects.Capability.ToUpperInvariant()]));
        Unsupported(() => (witness with { Version = 0 }).Validate());
        Unsupported(() => (witness with { Version = 2 }).Validate());
        Reject(() => (witness with { InstanceId = Guid.Empty }).Validate());
        Reject(() => (witness with { AdmissionId = Guid.Empty }).Validate());
        Reject(() => (witness with { WitnessId = Guid.Empty }).Validate());
        Reject(() => (witness with { Directory = new("docs", "../other") }).Validate());
        Reject(() => (witness with { Generation = "" }).Validate());
        Reject(() => (witness with { Generation = new string('G', 4097) }).Validate());
        Reject(() => (witness with { Generation = "generation\nother" }).Validate());
        Reject(() => (witness with { OriginalRevision = null! }).Validate());
        Reject(() => (witness with { ParentGeneration = null }).Validate());
        Reject(() => (witness with { RequestFingerprint = new string('a', 64) }).Validate());
        Reject(() => (witness with { BackendEvidence = "AQID\n" }).Validate());
        Reject(() => (witness with { BackendEvidence = "not-base64" }).Validate());
        Reject(() => (witness with { BackendEvidence = Convert.ToBase64String(new byte[65537]) }).Validate());
        var rootWitness = witness with { Directory = new("docs", ""), ParentGeneration = null };
        rootWitness.Validate();
        Reject(() => (rootWitness with { ParentGeneration = "invented-parent" }).Validate());
        var firstTransition = new AdapterOwnedParentTransition(witness, witness.OriginalRevision, "after-first-child", null, "BAUG");
        var first = new AdapterOwnedChildEffectReceipt(1, instance, admission,
            Guid.Parse("44444444-4444-4444-4444-444444444444"), Guid.Parse("55555555-5555-5555-5555-555555555555"),
            "Delete", new string('B', 64), new("docs", "tree/one.bin"), "first-original-child", null, [firstTransition]);
        first.Validate();
        var secondTransition = new AdapterOwnedParentTransition(witness, "after-first-child", "after-second-child",
            AdapterDirectoryEffects.Fingerprint(first), "BwgJ");
        AdapterDirectoryEffects.RequirePredecessor(secondTransition, first);
        Reject(() => (firstTransition with { BeforeRevision = "latest-observed-parent" }).Validate());
        Reject(() => AdapterDirectoryEffects.RequirePredecessor(secondTransition with { BeforeRevision = "later-parent" }, first));
        Reject(() => AdapterDirectoryEffects.RequirePredecessor(secondTransition with { PreviousReceiptFingerprint = new string('C', 64) }, first));
        Reject(() => AdapterDirectoryEffects.RequirePredecessor(secondTransition with { OriginalParent = witness with { Generation = "replacement" } }, first));
        Reject(() => AdapterDirectoryEffects.RequirePredecessor(secondTransition with { OriginalParent = witness with { WitnessId = Guid.NewGuid() } }, first));
        Reject(() => (first with { OperationId = Guid.Empty }).Validate());
        Reject(() => (first with { InstanceId = Guid.NewGuid() }).Validate());
        Reject(() => (first with { AdmissionId = Guid.NewGuid() }).Validate());
        Reject(() => (first with { Child = new("docs", "other/one.bin") }).Validate());
        Reject(() => (first with { Child = new("foreign", "tree/one.bin") }).Validate());
        Reject(() => (first with { Parents = [firstTransition, firstTransition] }).Validate());
        Reject(() => (first with { AcceptedRevision = "null-is-not-an-unknown-parent" }).Validate());
        Reject(() => (first with { OperationKind = "Upload" }).Validate());
        Reject(() => (first with { Child = new("docs", "") }).Validate());
        var sameParentMove = first with { OperationKind = "Move", AcceptedRevision = "accepted-move", Destination = new("docs", "tree/two.bin") };
        sameParentMove.Validate();
        Reject(() => (sameParentMove with { Destination = null }).Validate());
        Reject(() => (first with { Destination = new("docs", "tree/two.bin") }).Validate());
        var otherWitness = witness with
        {
            WitnessId = Guid.NewGuid(),
            Directory = new("archive", "branch"),
            Generation = "other-parent",
            OriginalRevision = "other-parent-revision",
            ParentGeneration = "archive-root"
        };
        var otherTransition = new AdapterOwnedParentTransition(otherWitness, otherWitness.OriginalRevision, "other-after", null, "CgsM");
        var crossRoot = sameParentMove with { Destination = new("archive", "branch/two.bin"), Parents = [firstTransition, otherTransition] };
        crossRoot.Validate();
        Reject(() => (crossRoot with { Parents = [firstTransition] }).Validate());
        Reject(() => (crossRoot with { Destination = new("archive", "foreign/two.bin") }).Validate());
        Reject(() => (crossRoot with { Parents = [firstTransition, otherTransition with { OriginalParent = otherWitness with { AdmissionId = Guid.NewGuid() } }] }).Validate());
        AdapterDirectoryEffects.RequireOriginalRequest(first, instance, admission, first.OperationId, first.RequestFingerprint);
        Reject(() => AdapterDirectoryEffects.RequireOriginalRequest(first, Guid.NewGuid(), admission, first.OperationId, first.RequestFingerprint));
        Reject(() => AdapterDirectoryEffects.RequireOriginalRequest(first, instance, Guid.NewGuid(), first.OperationId, first.RequestFingerprint));
        Reject(() => AdapterDirectoryEffects.RequireOriginalRequest(first, instance, admission, Guid.NewGuid(), first.RequestFingerprint));
        Reject(() => AdapterDirectoryEffects.RequireOriginalRequest(first, instance, admission, first.OperationId, new string('D', 64)));
        string encoded = System.Text.Encoding.UTF8.GetString(AdapterProtocolJson.Encode(first));
        var decoded = AdapterProtocolJson.Decode<AdapterOwnedChildEffectReceipt>(System.Text.Encoding.UTF8.GetBytes(encoded));
        decoded.Validate();
        Expect(AdapterDirectoryEffects.Fingerprint(first) == AdapterDirectoryEffects.Fingerprint(decoded), "Typed immutable receipt round trip");
        Expect(AdapterDirectoryEffects.Fingerprint(crossRoot) == AdapterDirectoryEffects.Fingerprint(crossRoot with { Parents = [otherTransition, firstTransition] }), "Ordinal canonical parent ordering");
        var request = new AdapterOperationRequest(first.OperationId, "docs", "tree/one.bin", "archive", "branch/two.bin", new("original-child-revision"));
        string fingerprint = AdapterDirectoryEffects.FingerprintRequest("Move", instance, admission, request, [witness, otherWitness]);
        Expect(fingerprint == AdapterDirectoryEffects.FingerprintRequest("Move", instance, admission, request, [otherWitness, witness]), "Original parent order does not change canonical request");
        Expect(fingerprint != AdapterDirectoryEffects.FingerprintRequest("Move", instance, admission, request with { Preconditions = new("latest-version") }, [witness, otherWitness]), "Original revision remains fingerprinted");
        Expect(fingerprint != AdapterDirectoryEffects.FingerprintRequest("Move", instance, admission, request with { DestinationPath = "branch/peer.bin" }, [witness, otherWitness]), "Original destination remains fingerprinted");
        Reject(() => AdapterDirectoryEffects.FingerprintRequest("Move", instance, admission, request, [witness, witness]));
        var second = first with
        {
            ReceiptId = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            Child = new("docs", "tree/two.bin"),
            OriginalChildGeneration = "second-original-child",
            RequestFingerprint = new string('C', 64),
            Parents = [secondTransition]
        };
        var requirements = new[] { new AdapterDirectoryChildRequirement(first.OperationId, first.RequestFingerprint, first.Child, first.OriginalChildGeneration),
            new AdapterDirectoryChildRequirement(second.OperationId, second.RequestFingerprint, second.Child, second.OriginalChildGeneration) };
        var retirement = new AdapterDirectoryRetirementEvidence(1, witness, new string('D', 64), requirements, [first, second],
            secondTransition.AfterRevision, AdapterDirectoryEffects.Fingerprint(second));
        retirement.Validate();
        Reject(() => (retirement with { AcceptedChildren = [first] }).Validate());
        Reject(() => (retirement with { AcceptedChildren = [second, first] }).Validate());
        Reject(() => (retirement with { AcceptedChildren = [first, first] }).Validate());
        Reject(() => (retirement with { FinalExpectedRevision = "latest-parent-stat" }).Validate());
        Reject(() => (retirement with { TerminalReceiptFingerprint = null }).Validate());
        Reject(() => (retirement with { OriginalDirectory = witness with { Generation = "replacement-parent" } }).Validate());
        Reject(() => (retirement with { RequiredChildren = [requirements[0], requirements[0]] }).Validate());
        Reject(() => (retirement with { RequiredChildren = [requirements[0], requirements[1] with { OriginalGeneration = "replacement-child" }] }).Validate());
        var empty = retirement with { RequiredChildren = [], AcceptedChildren = [], FinalExpectedRevision = witness.OriginalRevision, TerminalReceiptFingerprint = null };
        empty.Validate();
        Reject(() => (empty with { FinalExpectedRevision = "current-empty-parent" }).Validate());
        Reject(() => (empty with { OriginalDirectory = rootWitness }).Validate());
        Console.WriteLine("Original-directory evidence shape, scope, predecessor and canonical fingerprint checks passed; backend atomicity is not inferred.");
    }

    private static void Expect(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Expected invalid original-directory evidence to be refused.");
    }
    private static void Unsupported(Action action)
    {
        try { action(); }
        catch (NotSupportedException) { return; }
        throw new InvalidOperationException("Expected unsupported original-directory evidence capability/version to be refused.");
    }
}
