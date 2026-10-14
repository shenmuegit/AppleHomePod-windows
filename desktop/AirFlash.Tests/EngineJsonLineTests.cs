using AirFlash.Core;
using Xunit;

namespace AirFlash.Tests;

public sealed class EngineJsonLineTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task ExactLimitAllowsLineTerminators(string ending)
    {
        var payload = new string('x', 65_536);
        using var reader = new StringReader(payload + ending);
        Assert.Equal(payload, await ProcessEngineConnection.ReadBoundedLineAsync(reader, CancellationToken.None));
    }

    [Fact]
    public async Task OversizeUnterminatedLineStopsReadingAtLimit()
    {
        using var reader = new CountingTextReader(new string('x', 100_000));
        await Assert.ThrowsAsync<IOException>(() => ProcessEngineConnection.ReadBoundedLineAsync(reader, CancellationToken.None));
        Assert.Equal(65_537, reader.CharactersRead);
    }

    [Fact]
    public async Task ConsecutiveLinesAndEofDoNotLoseData()
    {
        using var reader = new StringReader("first\r\nsecond\n");
        Assert.Equal("first", await ProcessEngineConnection.ReadBoundedLineAsync(reader, CancellationToken.None));
        Assert.Equal("second", await ProcessEngineConnection.ReadBoundedLineAsync(reader, CancellationToken.None));
        Assert.Null(await ProcessEngineConnection.ReadBoundedLineAsync(reader, CancellationToken.None));
    }

    [Fact]
    public async Task PartialEofIsReturnedForJsonValidation()
    {
        using var reader = new StringReader("{\"event\":");
        Assert.Equal("{\"event\":", await ProcessEngineConnection.ReadBoundedLineAsync(reader, CancellationToken.None));
    }

    [Fact]
    public async Task WaitingReadCanBeCancelled()
    {
        using var reader = new WaitingReader();
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProcessEngineConnection.ReadBoundedLineAsync(reader, stop.Token).WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private sealed class CountingTextReader(string input) : StringReader(input)
    {
        public int CharactersRead { get; private set; }
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = base.ReadLine(); CharactersRead += line?.Length ?? 0;
            return ValueTask.FromResult(line);
        }
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = base.Read(buffer.Span); CharactersRead += count;
            return ValueTask.FromResult(count);
        }
    }

    private sealed class WaitingReader : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
