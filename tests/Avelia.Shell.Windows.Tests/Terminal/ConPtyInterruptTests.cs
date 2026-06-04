using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avelia.Shell.Windows.Terminal;
using Xunit;

namespace Avelia.Shell.Windows.Tests.Terminal;

/// <summary>
/// Fast-tier round-trip for the interrupt seam. ConPTY turns a lone <c>0x03</c>
/// on the input pipe into a <c>CTRL_C_EVENT</c> for the child's process group;
/// the only thing our code controls is that the byte we emit is exactly
/// <c>0x03</c> and nothing else. <see cref="ConPtySession.SendInterruptAsync"/>
/// and this test drive the same <see cref="ConPtySession.WriteInterruptAsync"/>
/// seam, so the invariant is checked without standing up a real pseudo-console.
/// </summary>
public class ConPtyInterruptTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public async Task SendInterrupt_writes_only_ETX_bytes(int times)
    {
        using var sink = new MemoryStream();

        for (int i = 0; i < times; i++)
            await ConPtySession.WriteInterruptAsync(sink, CancellationToken.None);

        byte[] written = sink.ToArray();
        Assert.Equal(times, written.Length);
        Assert.All(written, b => Assert.Equal((byte)0x03, b));
    }
}
