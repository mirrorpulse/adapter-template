using System.Text.Json;
using MirrorPulse.Adapter.Sdk;

string vector = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "move-v2.json")).Trim();
AdapterOperationRequest request = AdapterProtocolJson.Decode<AdapterOperationRequest>(System.Text.Encoding.UTF8.GetBytes(vector));
AdapterProtocolJson.ValidateMutation(request, requiresDestination: true);
Check(request.RootKey == "photos" && request.DestinationRootKey == "archive", "Cross-root golden addresses");
Check(System.Text.Encoding.UTF8.GetString(AdapterProtocolJson.Encode(request)) == vector, "Golden encoding");
using JsonDocument v1 = JsonDocument.Parse("{\"path\":\"a.txt\",\"futureField\":true}");
using JsonDocument binaryVector = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "binary-v2.json")));
JsonElement golden = binaryVector.RootElement;
var goldenChunk = new AdapterBinaryChunk(golden.GetProperty("requestId").GetGuid(), golden.GetProperty("instanceId").GetGuid(),
    golden.GetProperty("workerSessionId").GetGuid(), golden.GetProperty("streamId").GetGuid(), golden.GetProperty("offset").GetInt64(),
    Convert.FromHexString(golden.GetProperty("dataHex").GetString()!), golden.GetProperty("endOfStream").GetBoolean())
{ RootKey = golden.GetProperty("rootKey").GetString() };
Check(Convert.ToHexString(AdapterBinaryChunkV2Codec.Encode(goldenChunk)) == golden.GetProperty("payloadHex").GetString(), "Binary golden encoding");
Check(AdapterProtocolJson.ReadAddress(v1.RootElement, 1, "documents").RootKey == "documents", "Explicit v1 root binding");
Reject(() => AdapterProtocolJson.ReadAddress(v1.RootElement, 1), "Unbound v1");
Reject(() => AdapterProtocolJson.ReadAddress(v1.RootElement, 2), "Missing v2 root");
Reject(() => AdapterProtocolJson.ReadAddress(v1.RootElement, 3), "Unknown wire version");
Reject(() => new AdapterFileAddress("photos", "../escape").Validate(), "Parent traversal");
Reject(() => new AdapterFileAddress("photos", "C:/escape").Validate(), "Absolute path");
Reject(() => AdapterProtocolJson.ValidateMutation(request with { OperationId = Guid.Empty }, true), "Empty operation ID");
Reject(() => AdapterProtocolJson.ValidateMutation(request with { DestinationPath = null }, true), "Missing destination");
var modern = new AdapterHello(new(1, 2), AdapterHandshake.V2Capabilities);
Check(AdapterHandshake.Negotiate(modern, 2).SelectedVersion == 2, "New Host and Worker with two roots");
Check(AdapterHandshake.Negotiate(modern, 1, new(1, 1)).SelectedVersion == 1, "Old Host and new Worker");
Check(AdapterHandshake.Negotiate(new(new(1, 1), []), 1).SelectedVersion == 1, "New Host and old Worker");
Check(AdapterHandshake.Negotiate(new(new(1, 1), []), 2).ErrorCode == "MultipleRootsRequireProtocolV2", "No silent multi-root downgrade");
Check(AdapterHandshake.Negotiate(modern with { Capabilities = [] }, 2).ErrorCode == "RequiredCapabilityMissing", "Missing capabilities");
Check(AdapterHandshake.Negotiate(modern with { SupportedVersions = new(3, 3) }, 1).ErrorCode == "ProtocolVersionUnsupported", "Unsupported version");
Check(AdapterHandshake.Negotiate(modern with { SupportedVersions = new(3, 3) }, 1, new(3, 3)).ErrorCode == "ProtocolVersionUnsupported", "SDK never selects an unimplemented wire version");
Check(AdapterHandshake.Negotiate(modern, 0).ErrorCode == "InvalidRoots", "V2 requires at least one configured root");
AdapterRootBinding[] roots = [new("photos", true, new Dictionary<string, string>()), new("archive", false, new Dictionary<string, string>())];
AdapterHandshake.ValidateReady(new(2, AdapterHandshake.V2Capabilities, roots, new Dictionary<string, string>()), modern);
Reject(() => AdapterHandshake.ValidateReady(new(2, AdapterHandshake.V2Capabilities, [roots[0], roots[0]], new Dictionary<string, string>()), modern), "Duplicate root keys");
var chunk = new AdapterBinaryChunk(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0, new byte[] { 1, 2, 3 }, true) { RootKey = "photos" };
byte[] encodedChunk = AdapterBinaryChunkV2Codec.Encode(chunk);
Check(AdapterBinaryChunkV2Codec.Decode(encodedChunk).RootKey == "photos", "Binary root round trip");
AdapterStreamBinding Binding() => new(chunk.RequestId, chunk.InstanceId, chunk.WorkerSessionId, chunk.StreamId, "photos", 0, 3);
Binding().Accept(AdapterBinaryChunkV2Codec.Decode(encodedChunk));
Reject(() => Binding().Accept(chunk with { RootKey = "archive" }), "Wrong binary root");
Reject(() => Binding().Accept(chunk with { WorkerSessionId = Guid.NewGuid() }), "Wrong binary session");
Reject(() => Binding().Accept(chunk with { StreamId = Guid.NewGuid() }), "Wrong binary stream");
Reject(() => Binding().Accept(chunk with { Data = new byte[] { 1 }, EndOfStream = true }), "Short final chunk");
var duplicate = Binding();
duplicate.Accept(chunk);
Reject(() => duplicate.Accept(chunk), "Duplicate chunk");
Reject(() => AdapterBinaryChunkV2Codec.Encode(chunk with { Data = new byte[AdapterBinaryChunkV2Codec.MaximumChunkBytes + 1] }), "Oversized chunk");
encodedChunk[^1] ^= 1;
Reject(() => AdapterBinaryChunkV2Codec.Decode(encodedChunk), "Corrupt checksum");
string cache = Path.Combine(Path.GetTempPath(), "MirrorPulse-contract", Guid.NewGuid().ToString("N"));
try
{
    await using var lease = new AdapterTransferLease(cache);
    string leasedPath = lease.Path;
    CancellationToken leaseToken = lease.CancellationToken;
    await lease.Stream.WriteAsync(new byte[] { 1, 2, 3 });
    await lease.CancelAsync();
    Check(leaseToken.IsCancellationRequested && !File.Exists(leasedPath), "Cancellation deletes actual lease");
}
finally { Directory.Delete(cache); }
Console.WriteLine("Contract, handshake and binary boundary checks passed.");
string processCache = Path.Combine(Path.GetTempPath(), "MirrorPulse-conformance", Guid.NewGuid().ToString("N"));
try
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    string executable = Path.ChangeExtension(typeof(MirrorPulse.Adapter.SampleWorker.SampleWorkerMarker).Assembly.Location, ".exe");
    await MirrorPulse.Adapter.Conformance.AdapterConformanceRunner.RunAsync(executable, processCache, timeout.Token);
    Console.WriteLine("Actual memory Worker process conformance passed.");
}
finally { if (Directory.Exists(processCache)) Directory.Delete(processCache, recursive: true); }

static void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
}

static void Reject(Action action, string name)
{
    try { action(); }
    catch (InvalidDataException) { return; }
    throw new InvalidOperationException("Expected rejection: " + name);
}
