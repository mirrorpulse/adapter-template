using MirrorPulse.Adapter.Conformance;

if (args.Length is not (4 or 6) || args[0] != "--worker" || args[2] != "--transfer-cache" ||
    (args.Length == 6 && args[4] != "--worker-argument")) return 2;
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
await AdapterConformanceRunner.RunAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[3]), deadline.Token,
    args.Length == 6 ? [args[5]] : null);
Console.WriteLine("Worker v2 memory-source conformance passed.");
return 0;
