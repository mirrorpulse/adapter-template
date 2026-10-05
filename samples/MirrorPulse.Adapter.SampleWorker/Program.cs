using MirrorPulse.Adapter.SampleWorker;
using MirrorPulse.Adapter.Sdk;

AdapterWorkerProcessArguments arguments = AdapterWorkerProcessArguments.Parse(args);
await using AdapterNamedPipeClient pipe = await AdapterNamedPipeClient.ConnectAsync(arguments.PipeName, TimeSpan.FromSeconds(10));
await using var channel = new AdapterControlChannel(pipe, arguments.InstanceId, arguments.WorkerSessionId);
var offer = new AdapterHello(new(2, 2), AdapterHandshake.V2Capabilities);
Guid helloId = Guid.NewGuid();
await channel.SendAsync("Hello", helloId, false, offer);
AdapterControlFrame readyFrame = await channel.ReadAsync();
if (readyFrame.MessageType == "HandshakeRejected") return 4;
if (readyFrame.MessageType != "Ready" || !readyFrame.IsResponse || readyFrame.RequestId != helloId) return 4;
AdapterReady ready = AdapterProtocolJson.Decode<AdapterReady>(System.Text.Encoding.UTF8.GetBytes(readyFrame.Payload.GetRawText()));
AdapterHandshake.ValidateReady(ready, offer);
channel.SelectProtocol(ready.SelectedVersion);
string cache = Environment.GetEnvironmentVariable("MP_TRANSFER_CACHE_DIR")
    ?? throw new InvalidDataException("TransferCacheRequired");
await using var worker = new MemoryWorker(channel, arguments, ready.Roots, cache);
await channel.SendAsync("Connected", Guid.NewGuid(), false, new { });
await worker.RunAsync();
return 0;
