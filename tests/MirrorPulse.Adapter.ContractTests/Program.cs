using System.Text.Json;
using MirrorPulse.Adapter.Sdk;

string vector = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "move-v2.json")).Trim();
AdapterOperationRequest request = AdapterProtocolJson.Decode<AdapterOperationRequest>(System.Text.Encoding.UTF8.GetBytes(vector));
AdapterProtocolJson.ValidateMutation(request, requiresDestination: true);
Check(request.RootKey == "photos" && request.DestinationRootKey == "archive", "Cross-root golden addresses");
Check(System.Text.Encoding.UTF8.GetString(AdapterProtocolJson.Encode(request)) == vector, "Golden encoding");
using JsonDocument v1 = JsonDocument.Parse("{\"path\":\"a.txt\",\"futureField\":true}");
Check(AdapterProtocolJson.ReadAddress(v1.RootElement, 1, "documents").RootKey == "documents", "Explicit v1 root binding");
Reject(() => AdapterProtocolJson.ReadAddress(v1.RootElement, 1), "Unbound v1");
Reject(() => AdapterProtocolJson.ReadAddress(v1.RootElement, 2), "Missing v2 root");
Reject(() => AdapterProtocolJson.ReadAddress(v1.RootElement, 3), "Unknown wire version");
Reject(() => new AdapterFileAddress("photos", "../escape").Validate(), "Parent traversal");
Reject(() => new AdapterFileAddress("photos", "C:/escape").Validate(), "Absolute path");
Reject(() => AdapterProtocolJson.ValidateMutation(request with { OperationId = Guid.Empty }, true), "Empty operation ID");
Reject(() => AdapterProtocolJson.ValidateMutation(request with { DestinationPath = null }, true), "Missing destination");
Console.WriteLine("Contract checks passed.");

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
