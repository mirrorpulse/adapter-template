namespace MirrorPulse.Adapter.Sdk;

/// <summary>A temporary transfer file is deleted on close, including cancellation.</summary>
public sealed class AdapterTransferLease : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private bool _disposed;

    public AdapterTransferLease(string transferCacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transferCacheDirectory);
        Directory.CreateDirectory(transferCacheDirectory);
        Path = System.IO.Path.Combine(transferCacheDirectory, Guid.NewGuid().ToString("N") + ".transfer");
        Stream = new FileStream(Path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
    }

    public string Path { get; }
    public FileStream Stream { get; }
    public CancellationToken CancellationToken => _cancellation.Token;

    public async ValueTask CancelAsync()
    {
        if (_disposed) return;
        await _cancellation.CancelAsync().ConfigureAwait(false);
        await DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await Stream.DisposeAsync().ConfigureAwait(false);
        _cancellation.Dispose();
    }
}
