using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Pi.Mcp.Protocol;
using Pi.Mcp.Transports;
using Xunit;

namespace Pi.Mcp.Tests;

/// <summary>
/// 请求超时必须覆盖发送阶段：Streamable HTTP 的 POST 以 SSE 应答时，服务器可能迟迟不发应答，
/// 也可能发了应答却按规范的 SHOULD 继续保持流打开。两种情况都不能让请求挂死。
/// </summary>
public class McpClientHttpTimeoutTests
{
    /// <summary>永远不产出数据、也不结束的只读流（挂死的服务器）。</summary>
    private sealed class NeverEndingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>先交付前缀，然后保持打开（合规但不关闭流的服务器）。</summary>
    private sealed class PrefixThenBlockStream(byte[] prefix) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < prefix.Length)
            {
                var length = Math.Min(buffer.Length, prefix.Length - _position);
                prefix.AsMemory(_position, length).CopyTo(buffer);
                _position += length;
                return length;
            }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// 假 MCP 服务器：initialize 以 JSON 应答并下发会话 id；通知回 202；其余请求打开 SSE 流，
    /// 按 <paramref name="answerRequests"/> 决定是先写出应答再保持打开，还是什么都不发。
    /// </summary>
    private sealed class FakeMcpHandler(bool answerRequests) : HttpMessageHandler
    {
        public int Requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var node = body.Length > 0 ? JsonNode.Parse(body) : null;
            var method = node?["method"]?.GetValue<string>();
            if (method == "initialize")
            {
                var reply = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = node!["id"]!.DeepClone(),
                    ["result"] = new JsonObject
                    {
                        ["protocolVersion"] = node["params"]!["protocolVersion"]!.DeepClone(),
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "fake", ["version"] = "1" },
                    },
                };
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(reply.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                response.Headers.Add("Mcp-Session-Id", "s1");
                return response;
            }
            if (node?["id"] is null) return new HttpResponseMessage(HttpStatusCode.Accepted);

            Interlocked.Increment(ref Requests);
            Stream stream = new NeverEndingStream();
            if (answerRequests)
            {
                var reply = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = node!["id"]!.DeepClone(),
                    ["result"] = new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "done" }),
                    },
                };
                stream = new PrefixThenBlockStream(Encoding.UTF8.GetBytes($"event: message\ndata: {reply.ToJsonString()}\n\n"));
            }
            var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    private static async Task<(McpClient Client, StreamableHttpTransport Transport)> ConnectAsync(
        FakeMcpHandler handler, long timeoutMs)
    {
        var transport = new StreamableHttpTransport(
            new StreamableHttpTransportOptions { Url = "http://fake/mcp", OpenGetStream = false },
            new HttpClient(handler));
        var client = new McpClient(new McpClientOptions { Name = "t", Version = "1", RequestTimeoutMs = timeoutMs });
        await client.ConnectAsync(transport);
        return (client, transport);
    }

    [Fact]
    public async Task RequestTimeoutCoversAnSseResponseThatNeverArrives()
    {
        var handler = new FakeMcpHandler(answerRequests: false);
        var (client, _) = await ConnectAsync(handler, timeoutMs: 1000);

        var stopwatch = Stopwatch.StartNew();
        var call = client.CallToolAsync("slow");
        var winner = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(8)));
        Assert.True(ReferenceEquals(winner, call), $"still pending after {stopwatch.ElapsedMilliseconds} ms");

        var error = await Assert.ThrowsAsync<McpTimeoutError>(() => call);
        Assert.Equal(1000, error.TimeoutMs);
        Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"took {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(1, handler.Requests);
        await client.CloseAsync();
    }

    [Fact]
    public async Task ResponseOnAnSseStreamThatStaysOpenCompletesTheRequest()
    {
        var handler = new FakeMcpHandler(answerRequests: true);
        var (client, _) = await ConnectAsync(handler, timeoutMs: 30_000);

        var stopwatch = Stopwatch.StartNew();
        var call = client.CallToolAsync("quick");
        var winner = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(8)));
        Assert.True(ReferenceEquals(winner, call), $"still pending after {stopwatch.ElapsedMilliseconds} ms");

        var result = await call;
        Assert.Equal("done", result.Content[0].Raw["text"]?.GetValue<string>());
        Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"took {stopwatch.ElapsedMilliseconds} ms");
        await client.CloseAsync();
    }

    [Fact]
    public async Task CallerCancellationDuringTheSendIsStillAnAbort()
    {
        var handler = new FakeMcpHandler(answerRequests: false);
        var (client, _) = await ConnectAsync(handler, timeoutMs: 30_000);

        using var abort = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<McpAbortError>(() => client.CallToolAsync("slow", null, null, abort.Token));
        await client.CloseAsync();
    }
}
