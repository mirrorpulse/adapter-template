using MirrorPulse.Adapter.Conformance;

if (args.Length != 4 || args[0] != "--worker" || args[2] != "--transfer-cache") return 2;
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
await AdapterConformanceRunner.RunAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[3]), deadline.Token);
Console.WriteLine("Worker v2 memory-source conformance passed.");
return 0;
