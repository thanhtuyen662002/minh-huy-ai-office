using System.Security.Cryptography;

namespace MinhHuy.AIOffice.Agent.Worker;

/// <summary>Fixed adapter-owned accepted entity bytes, including every Stream write overload.</summary>
internal sealed class FiniteResponseSink(CancellationToken cancellation) : Stream
{
    private readonly object gate = new();
    private readonly byte[] buffer = new byte[StructuredResponsesPolicy.EntityByteLimit];
    private int length;
    private bool failed;
    private bool sealedOrDisposed;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public ReadOnlyMemory<byte> Seal()
    {
        lock (gate)
        {
            if (failed || sealedOrDisposed) throw new IOException("Response sink is unavailable.");
            cancellation.ThrowIfCancellationRequested();
            sealedOrDisposed = true;
            return buffer.AsMemory(0, length);
        }
    }

    public override void Write(ReadOnlySpan<byte> value)
    {
        lock (gate)
        {
            if (cancellation.IsCancellationRequested) { failed = true; cancellation.ThrowIfCancellationRequested(); }
            if (sealedOrDisposed || failed || value.Length > buffer.Length - length)
            {
                failed = true;
                throw new IOException("Response sink bound exceeded.");
            }
            value.CopyTo(buffer.AsSpan(length));
            length += value.Length;
        }
    }

    public override void Write(byte[] value, int offset, int count) => Write(value.AsSpan(offset, count));
    public override void WriteByte(byte value) => Write(new ReadOnlySpan<byte>(in value));
    public override Task WriteAsync(byte[] value, int offset, int count, CancellationToken token)
    {
        CheckWriteToken(token);
        Write(value, offset, count);
        return Task.CompletedTask;
    }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> value, CancellationToken token = default)
    {
        CheckWriteToken(token);
        Write(value.Span);
        return ValueTask.CompletedTask;
    }
    private void CheckWriteToken(CancellationToken token)
    {
        if (!token.IsCancellationRequested) return;
        lock (gate) { failed = true; }
        token.ThrowIfCancellationRequested();
    }
    public override void Flush() { cancellation.ThrowIfCancellationRequested(); }
    public override Task FlushAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Flush(); return Task.CompletedTask; }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) lock (gate) { sealedOrDisposed = true; CryptographicOperations.ZeroMemory(buffer); }
        base.Dispose(disposing);
    }
}
