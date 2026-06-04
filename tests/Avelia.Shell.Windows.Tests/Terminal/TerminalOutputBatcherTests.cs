using System.Linq;
using System.Text;
using Avelia.Shell.Windows.Terminal;
using Xunit;

namespace Avelia.Shell.Windows.Tests.Terminal;

public class TerminalOutputBatcherTests
{
    [Fact]
    public void Drain_coalesces_appended_chunks_in_order()
    {
        var batcher = new TerminalOutputBatcher();
        batcher.Append("foo"u8);
        batcher.Append("bar"u8);
        batcher.Append("baz"u8);

        Assert.True(batcher.TryDrain(out var batch));
        Assert.Equal("foobarbaz", Encoding.UTF8.GetString(batch));
    }

    [Fact]
    public void Drain_on_an_empty_batcher_reports_nothing()
    {
        var batcher = new TerminalOutputBatcher();

        Assert.False(batcher.TryDrain(out var batch));
        Assert.Empty(batch);
    }

    [Fact]
    public void Drain_resets_pending_so_a_second_drain_is_empty()
    {
        var batcher = new TerminalOutputBatcher();
        batcher.Append("data"u8);

        Assert.True(batcher.TryDrain(out _));
        Assert.Equal(0, batcher.PendingBytes);
        Assert.False(batcher.TryDrain(out _));
    }

    [Fact]
    public void Empty_append_is_a_no_op()
    {
        var batcher = new TerminalOutputBatcher();
        batcher.Append(System.ReadOnlySpan<byte>.Empty);

        Assert.Equal(0, batcher.PendingBytes);
        Assert.False(batcher.TryDrain(out _));
    }

    [Fact]
    public void PendingBytes_tracks_appended_length()
    {
        var batcher = new TerminalOutputBatcher();
        batcher.Append(Enumerable.Repeat((byte)0x41, 100).ToArray());

        Assert.Equal(100, batcher.PendingBytes);
    }
}
