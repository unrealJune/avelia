using System;
using System.Buffers;

namespace Avelia.Shell.Windows.Terminal;

/// <summary>
/// Coalesces the many small byte chunks a ConPTY read loop produces into one
/// contiguous buffer per drain. The renderer pumps a drain on an ~8ms cadence
/// (one frame), so hundreds of tiny <c>ReadAllAsync</c> yields collapse into a
/// single <c>xterm.write()</c> instead of one round-trip per chunk.
///
/// Thread-safe: the ConPTY read loop appends while the frame timer drains.
/// </summary>
internal sealed class TerminalOutputBatcher
{
    private readonly object _gate = new();
    private readonly ArrayBufferWriter<byte> _buffer = new();

    /// <summary>Bytes buffered and not yet drained.</summary>
    public int PendingBytes
    {
        get
        {
            lock (_gate)
                return _buffer.WrittenCount;
        }
    }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;

        lock (_gate)
            _buffer.Write(bytes);
    }

    /// <summary>
    /// Hand back everything buffered since the last drain, or <c>false</c> when
    /// nothing is pending (so the caller can skip a no-op renderer round-trip).
    /// </summary>
    public bool TryDrain(out byte[] batch)
    {
        lock (_gate)
        {
            if (_buffer.WrittenCount == 0)
            {
                batch = Array.Empty<byte>();
                return false;
            }

            batch = _buffer.WrittenSpan.ToArray();
            _buffer.Clear();
            return true;
        }
    }
}
