using System.Text;
using System.Text.Json.Nodes;
using Pi.Mcp.Transports;
using Xunit;

namespace Pi.Mcp.Tests;

/// <summary>
/// SSE 解析器（TS <c>consumeSseStream</c>）在字节流边界上的行为：一次 ReadAsync 可能把多字节
/// UTF-8 序列截成两半，解码必须跨块进行。
/// </summary>
public class SseStreamTests
{
    /// <summary>每次只交付固定字节数的只读流，模拟慢速网络。</summary>
    private sealed class ChunkedStream(byte[] data, int chunk) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var length = Math.Min(Math.Min(count, chunk), data.Length - _position);
            Array.Copy(data, _position, buffer, offset, length);
            _position += length;
            return length;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(13)]
    public async Task MultiByteCharactersSplitAcrossReadsDecodeIntact(int chunk)
    {
        const string text = "你好，世界 — émoji 😀 ok";
        var json = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["result"] = new JsonObject { ["text"] = text },
        }.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        var bytes = Encoding.UTF8.GetBytes($"event: message\ndata: {json}\n\n");

        var events = new List<SseEvent>();
        await SseStream.ConsumeAsync(new ChunkedStream(bytes, chunk), new ConsumeSseOptions
        {
            OnEvent = events.Add,
        });

        var @event = Assert.Single(events);
        Assert.Equal("message", @event.Event);
        var received = JsonNode.Parse(@event.Data)!["result"]!["text"]!.GetValue<string>();
        Assert.DoesNotContain('�', received);
        Assert.Equal(text, received);
    }

    [Fact]
    public async Task TrailingLineWithoutNewlineIsStillDispatched()
    {
        var bytes = Encoding.UTF8.GetBytes("data: 你好\n\ndata: 末尾");
        var events = new List<SseEvent>();
        await SseStream.ConsumeAsync(new ChunkedStream(bytes, 2), new ConsumeSseOptions { OnEvent = events.Add });

        Assert.Equal(["你好", "末尾"], events.Select(e => e.Data).ToArray());
    }
}
