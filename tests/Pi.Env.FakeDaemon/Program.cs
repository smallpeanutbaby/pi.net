using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

// 测试用假守护进程：模拟 pi-env 的启动形状（`serve --token <hex>`）与几种传输边界。
// 用法：Pi.Env.FakeDaemon serve --token <hex> --script <noise|corrupt|garbage-json|hello|echo>
var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
if (!arguments.Contains("serve", StringComparer.Ordinal))
{
    Console.Error.WriteLine("usage: Pi.Env.FakeDaemon serve --token <hex> --script <name>");
    return 2;
}

string? Value(string name)
{
    var index = Array.IndexOf(arguments, name);
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}

var token = Value("--token") ?? "";
var script = Value("--script") ?? "hello";
var stdout = Console.OpenStandardOutput();

void Write(byte[] bytes)
{
    stdout.Write(bytes, 0, bytes.Length);
    stdout.Flush();
}

// 登录 shell 的横幅：客户端必须丢弃同步行之前的一切。
if (script == "noise") Write(Encoding.UTF8.GetBytes("banner from the login shell\n"));
Write(Encoding.UTF8.GetBytes($"PI-ENV {token}\n"));

switch (script)
{
    case "corrupt":
        // 帧长度 5 < 9：长度字段本身非法。
        Write([0, 0, 0, 5, 1, 0, 0, 0, 1, 0, 0, 0, 0]);
        break;
    case "garbage-json":
        Write(Frame(2, 1, Encoding.UTF8.GetBytes("not json")));
        break;
    case "echo":
        // 完整的请求/应答循环：按 id 应答 hello（报告真实 pid），`echo` 原样返回 n，
        // `spew` 先往 stderr 灌 1 MiB 再应答（制造没人读 stderr 时的管道阻塞）。
        return Serve();
    default:
        Write(Frame(2, 1, Encoding.UTF8.GetBytes(Hello(4242))));
        break;
}

// 守护进程活到 stdin 结束（客户端关闭时）。
try
{
    Console.OpenStandardInput().CopyTo(Stream.Null);
}
catch (IOException)
{
}

return 0;

int Serve()
{
    var stdin = Console.OpenStandardInput();
    var stderr = Console.OpenStandardError();
    var header = new byte[4];
    while (true)
    {
        if (!ReadExactly(stdin, header)) return 0;
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        var body = new byte[length];
        if (!ReadExactly(stdin, body)) return 0;
        var type = body[0];
        var id = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(1));
        var jsonLength = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(5));
        if (type != 1) continue; // ping / cancel：忽略。
        JsonObject request;
        try
        {
            request = JsonNode.Parse(body.AsSpan(9, (int)jsonLength)) as JsonObject
                ?? throw new InvalidOperationException("not an object");
        }
        catch (Exception error)
        {
            // 交错或损坏的帧：报告给测试（stderr）并以错误帧应答。
            Console.Error.WriteLine($"BAD FRAME id={id}: {error.Message}");
            Write(Frame(3, id, Encoding.UTF8.GetBytes(
                new JsonObject { ["code"] = "bad_frame", ["message"] = error.Message }.ToJsonString())));
            continue;
        }

        var op = request["op"]?.GetValue<string>();
        string reply;
        switch (op)
        {
            case "hello":
                reply = Hello(Environment.ProcessId);
                break;
            case "spew":
                var junk = Encoding.UTF8.GetBytes(new string('x', 1023) + "\n");
                for (var i = 0; i < 1024; i++) stderr.Write(junk, 0, junk.Length);
                stderr.Flush();
                reply = new JsonObject { ["spewed"] = true }.ToJsonString();
                break;
            default:
                reply = new JsonObject { ["echo"] = request["n"]?.DeepClone() }.ToJsonString();
                break;
        }

        Write(Frame(2, id, Encoding.UTF8.GetBytes(reply)));
    }
}

static bool ReadExactly(Stream stream, byte[] buffer)
{
    var offset = 0;
    while (offset < buffer.Length)
    {
        var read = stream.Read(buffer, offset, buffer.Length - offset);
        if (read == 0) return false;
        offset += read;
    }

    return true;
}

static byte[] Frame(byte type, uint id, byte[] json, byte[]? payload = null)
{
    payload ??= [];
    var frame = new byte[13 + json.Length + payload.Length];
    BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)(9 + json.Length + payload.Length));
    frame[4] = type;
    BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(5), (uint)id);
    BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(9), (uint)json.Length);
    json.CopyTo(frame, 13);
    payload.CopyTo(frame, 13 + json.Length);
    return frame;
}

static string Hello(long pid) => new JsonObject
{
    ["protocol"] = 1,
    ["version"] = "fake",
    ["os"] = "windows",
    ["arch"] = "x64",
    ["home"] = "/home/fake",
    ["tmpdir"] = "/tmp",
    ["separator"] = "/",
    ["cwd"] = "/",
    ["driveCwds"] = new JsonObject(),
    ["pid"] = pid,
}.ToJsonString();
