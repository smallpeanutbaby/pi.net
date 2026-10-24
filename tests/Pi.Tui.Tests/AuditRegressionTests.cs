using System.Globalization;
using System.Text;
using Pi.Tui;
using Pi.Tui.Components;
using Pi.Tui.Marked;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>
/// 审计回归（T35）：这些输入在 TS 侧只是普通内容，在端口里却曾让渲染/输入线程抛出未处理异常并杀死进程。
/// 每个用例对应一条已复现的发现；修复前它们都会失败（抛出）。
/// </summary>
public class MarkdownLexerRobustnessTests
{
    internal static MarkdownTheme PlainTheme() => new()
    {
        Heading = s => s,
        Link = s => s,
        LinkUrl = s => s,
        Code = s => s,
        CodeBlock = s => s,
        CodeBlockBorder = s => s,
        Quote = s => s,
        QuoteBorder = s => s,
        Hr = s => s,
        ListBullet = s => s,
        Bold = s => s,
        Italic = s => s,
        Strikethrough = s => s,
        Underline = s => s,
    };

    private static string[] Render(string markdown, int width = 40) =>
        new Markdown(markdown, 0, 0, PlainTheme()).Render(width);

    /// <summary>
    /// 引用块里的列表带懒延续行时，marked 的 blockquote tokenizer 会把 <c>raw</c> 报成比剩余源码多一个
    /// 字符（<c>"".split("\n")</c> 是 <c>[""]</c>，多拼了一个 "\n"）。JS 的 <c>substring</c> 会把越界钳到
    /// 空串，C# 的范围切片却抛 <see cref="ArgumentOutOfRangeException"/>。
    /// </summary>
    [Theory]
    [InlineData("> - item\ncontinued")]
    [InlineData("> 1. item\n😀 more")]
    [InlineData(">1.\nx")]
    [InlineData("> *\n🚀 launch")]
    [InlineData(">1.\n🇯🇵")]
    [InlineData(">1.\n😀")]
    [InlineData("> 1.\n😀")]
    [InlineData("> -\n😀")]
    [InlineData(">-\n😀")]
    [InlineData("> 1.\n中")]
    [InlineData("> 1.\n\U00020000")]
    [InlineData("> 2)\n😀")]
    [InlineData("- a\n  >1.\n  😀")]
    [InlineData("> 1.\n> 😀")]
    [InlineData("> - \n😀")]
    [InlineData("> > - a\n> b\nc")]
    public void BlockquoteWithLazyListContinuationRendersInsteadOfThrowing(string markdown)
    {
        var lines = Render(markdown);

        Assert.NotEmpty(lines);
        // 词法层面：整段仍是一个引用块，列表在其中。
        var tokens = MarkedLexer.Lex(markdown.Replace("- a\n  ", ""));
        Assert.NotEmpty(tokens);
    }

    [Fact]
    public void BlockquoteWithLazyListContinuationKeepsTheContinuationInTheItem()
    {
        var tokens = MarkedLexer.Lex("> - item\ncontinued");

        var quote = Assert.Single(tokens);
        Assert.Equal("blockquote", quote.Type);
        var list = Assert.Single(quote.Tokens!);
        Assert.Equal("list", list.Type);
        var item = Assert.Single(list.Items!);
        Assert.Equal("item\ncontinued", item.Text);

        var rendered = string.Join("\n", Render("> - item\ncontinued"));
        Assert.Contains("item", rendered, StringComparison.Ordinal);
        Assert.Contains("continued", rendered, StringComparison.Ordinal);
    }

    /// <summary>与 marked 一致：只有 <c>substring</c> 语义能让「raw 比源码长一个字符」无害。</summary>
    [Fact]
    public void JsSubstringClampsLikeJavaScript()
    {
        Assert.Equal("", JsString.Substring("abc", 5));
        Assert.Equal("", JsString.Substring("abc", 3));
        Assert.Equal("c", JsString.Substring("abc", 2));
        // substring(0, negative) 钳到 0，而不是像 slice 那样从尾部数。
        Assert.Equal("", JsString.Substring("abc", 0, -2));
        Assert.Equal("ab", JsString.Slice("abc", 0, -1));
        // 越界的 end 钳到长度；start > end 则交换。
        Assert.Equal("bc", JsString.Substring("abc", 1, 99));
        Assert.Equal("b", JsString.Substring("abc", 2, 1));
    }

    /// <summary>
    /// .NET 的 <c>\d</c> 匹配所有 Unicode 十进制数字（阿拉伯-印度数字、天城文数字、全角数字），
    /// marked 的列表规则因此把它们当有序列表，随后 <c>int.Parse</c> 抛 <see cref="FormatException"/>。
    /// TS 的 <c>\d</c> 只匹配 0-9，这些行是普通段落（T5）。
    /// </summary>
    [Theory]
    [InlineData("١. البند الأول\n٢. البند الثاني", "البند")]
    [InlineData("१. पहला", "पहला")]
    [InlineData("１. 第一项", "第一项")]
    public void NonAsciiDigitsDoNotStartAnOrderedList(string markdown, string expectedText)
    {
        var tokens = MarkedLexer.Lex(markdown);
        Assert.All(tokens, token => Assert.NotEqual("list", token.Type));
        Assert.Contains(tokens, token => token.Type == "paragraph");

        var rendered = string.Join("\n", Render(markdown));
        Assert.Contains(expectedText, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void AsciiDigitsStillStartAnOrderedList()
    {
        var tokens = MarkedLexer.Lex("1. item\n2. two");

        var list = Assert.Single(tokens);
        Assert.Equal("list", list.Type);
        Assert.True(list.Ordered);
        Assert.Equal(1, list.Start);
        Assert.Equal(2, list.Items!.Count);
    }

    /// <summary>
    /// 有界模糊测试（对应审计用的 mdfuzz）：固定种子、小字母表、2000 条输入，渲染绝不能抛出。
    /// 真实崩溃用例（<c>">1.\n🇯🇵"</c>）就是它找到的。
    /// </summary>
    [Fact]
    public void RandomMarkdownNeverThrows()
    {
        string[] atoms =
        [
            "a", "word", " ", "  ", "   ", "    ", "\n", "\n\n", "> ", ">", "- ", "* ", "+ ", "1. ", "2) ", "#", "## ",
            "```", "~~~", "`", "**", "*", "_", "__", "~~", "~", "[", "]", "(", ")", "[x](http://a)", "![i](u)", "<", ">",
            "<div>", "</div>", "|", "| a | b |\n|---|---|\n", "---", "===", "\\", "$", "$$", "\\frac{a}{b}", "^", "&amp;",
            "&#123;", "\t", "中文", "😀", "🇯🇵", "é", "http://x.y/z", "www.a.com", "a@b.co", "[ref]: http://x", "[ref]",
            "- [ ] ", "- [x] ", "1.", "-", "\r\n", "\r", "<!--", "-->", "  \n", "١. ", "१. ", "１. ",
        ];
        var random = new Random(20261022);
        var theme = PlainTheme();
        var failures = new List<string>();

        for (var i = 0; i < 2000; i++)
        {
            var builder = new StringBuilder();
            var count = random.Next(1, 20);
            for (var k = 0; k < count; k++)
            {
                builder.Append(atoms[random.Next(atoms.Length)]);
            }
            var source = builder.ToString();
            var width = random.Next(5, 60);
            try
            {
                new Markdown(source, 0, 0, theme).Render(width);
            }
            catch (Exception exception)
            {
                failures.Add($"{exception.GetType().Name} for {System.Text.Json.JsonSerializer.Serialize(source)} w={width}");
                if (failures.Count >= 5)
                {
                    break;
                }
            }
        }

        Assert.Empty(failures);
    }
}

/// <summary>渲染与 UI 状态的跨线程串行化（T35），对应审计复现的 <c>render</c> / <c>overlay</c> 模式。</summary>
public class TuiRenderSerializationTests
{
    /// <summary>慢组件：记录并发 Render 的最大深度。</summary>
    private sealed class SlowComponent : IComponent, IFocusable
    {
        private int _active;
        private int _maxActive;
        private int _renders;

        public int MaxActive => Volatile.Read(ref _maxActive);

        public int Renders => Volatile.Read(ref _renders);

        public bool Focused { get; set; }

        public string[] Render(int width)
        {
            var now = Interlocked.Increment(ref _active);
            Interlocked.Increment(ref _renders);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxActive))
                && Interlocked.CompareExchange(ref _maxActive, now, seen) != seen)
            {
            }
            Thread.Sleep(15);
            Interlocked.Decrement(ref _active);
            return ["line " + _renders];
        }

        public void HandleInput(string data)
        {
        }
    }

    /// <summary>可以在第一次 Render 里停住的组件，用来制造「渲染进行中」的窗口。</summary>
    private sealed class BlockingComponent : IComponent, IFocusable
    {
        private int _renders;

        public ManualResetEventSlim Entered { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        public int Renders => Volatile.Read(ref _renders);

        public bool Focused { get; set; }

        public string[] Render(int width)
        {
            if (Interlocked.Increment(ref _renders) == 1)
            {
                Entered.Set();
                Release.Wait(TimeSpan.FromSeconds(5));
            }
            return ["x"];
        }

        public void HandleInput(string data)
        {
        }
    }

    private sealed class OverlayBox : IComponent
    {
        public string[] Render(int width)
        {
            Thread.SpinWait(2000);
            return ["overlay", "line2"];
        }
    }

    /// <summary>
    /// 按键在 ReadKey 线程、渲染在 Timer / 线程池线程、RenderNow 在调用方线程：三路同时触发时
    /// 过去最多有 3 个 DoRender 并发对同一份 <c>_previousLines</c> 做差分。现在任意时刻最多一个。
    /// </summary>
    [Fact]
    public void ConcurrentRequestsNeverRunTwoRendersAtOnce()
    {
        var terminal = new StringTerminal(80, 24);
        var tui = new TuiMainScreen(terminal);
        var component = new SlowComponent();
        tui.AddChild(component);
        tui.SetFocus(component);
        tui.Start();
        try
        {
            Thread.Sleep(50);
            var input = new Thread(() =>
            {
                for (var i = 0; i < 40; i++)
                {
                    terminal.SendInput("a");
                    Thread.Sleep(4);
                }
            });
            var agent = new Thread(() =>
            {
                for (var i = 0; i < 40; i++)
                {
                    tui.RequestRender();
                    Thread.Sleep(3);
                }
            });
            var direct = new Thread(() =>
            {
                for (var i = 0; i < 10; i++)
                {
                    tui.RenderNow();
                    Thread.Sleep(10);
                }
            });
            input.Start();
            agent.Start();
            direct.Start();
            input.Join();
            agent.Join();
            direct.Join();
            Thread.Sleep(200);

            Assert.True(component.Renders >= 10, $"expected several renders, got {component.Renders}");
            Assert.Equal(1, component.MaxActive);
        }
        finally
        {
            tui.Stop();
        }
    }

    /// <summary>渲染进行中到达的请求（计时器、立即渲染、RequestRender）合并成恰好一次后续渲染，不会丢也不会放大。</summary>
    [Fact]
    public void RequestsDuringARenderCoalesceIntoExactlyOneFollowUp()
    {
        var terminal = new StringTerminal(80, 24);
        var tui = new TuiMainScreen(terminal);
        var component = new BlockingComponent();
        tui.AddChild(component);
        tui.SetFocus(component);
        tui.Start();
        try
        {
            Assert.True(component.Entered.Wait(TimeSpan.FromSeconds(5)), "first render never started");

            // 渲染线程停在 Render 里；此时从别的线程发出三种请求。
            tui.RequestRender();
            tui.RequestRender();
            terminal.SendInput("a"); // RequestImmediateRender 路径
            Thread.Sleep(60);
            Assert.Equal(1, component.Renders);

            component.Release.Set();
            var deadline = Environment.TickCount64 + 3000;
            while (component.Renders < 2 && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(5);
            }
            Thread.Sleep(150);

            Assert.Equal(2, component.Renders);
        }
        finally
        {
            tui.Stop();
        }
    }

    /// <summary>RenderNow 保持同步语义：等正在进行的渲染结束后自己再渲染一次，而不是被合并掉。</summary>
    [Fact]
    public void RenderNowWaitsForTheRenderInProgressAndThenRenders()
    {
        var terminal = new StringTerminal(80, 24);
        var tui = new TuiMainScreen(terminal);
        var component = new BlockingComponent();
        tui.AddChild(component);
        tui.Start();
        try
        {
            Assert.True(component.Entered.Wait(TimeSpan.FromSeconds(5)), "first render never started");

            using var finished = new ManualResetEventSlim(false);
            var renderNow = new Thread(() =>
            {
                tui.RenderNow();
                finished.Set();
            });
            renderNow.Start();
            Thread.Sleep(60);
            Assert.False(finished.IsSet);
            Assert.Equal(1, component.Renders);

            component.Release.Set();
            Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));
            renderNow.Join();
            Assert.True(component.Renders >= 2);
        }
        finally
        {
            tui.Stop();
        }
    }

    /// <summary>
    /// 对应审计复现的 <c>overlay</c> 模式：渲染线程循环 RenderNow，另一线程不停 ShowOverlay / Hide。
    /// 过去每秒数千次「Collection was modified」/ NullReferenceException，最终未处理崩溃。
    /// </summary>
    [Fact]
    public void ShowingAndHidingOverlaysWhileRenderingNeverThrows()
    {
        var terminal = new StringTerminal(80, 24);
        var tui = new TuiMainScreen(terminal);
        tui.AddChild(new Text("base", 0, 0));
        var stop = false;
        var errors = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var renderer = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                try
                {
                    tui.RenderNow();
                }
                catch (Exception exception)
                {
                    var frame = exception.StackTrace?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("Pi.Tui", StringComparison.Ordinal)) ?? "";
                    errors.AddOrUpdate(exception.GetType().Name + ": " + exception.Message + " @ " + frame, 1, (_, n) => n + 1);
                }
            }
        });
        renderer.Start();

        var handles = new List<IOverlayHandle>();
        var deadline = Environment.TickCount64 + 1500;
        var shown = 0;
        try
        {
            while (Environment.TickCount64 < deadline)
            {
                handles.Add(tui.ShowOverlay(new OverlayBox(), new OverlayOptions { NonCapturing = true }));
                shown++;
                if (handles.Count > 3)
                {
                    handles[0].Hide();
                    handles.RemoveAt(0);
                }
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            renderer.Join();
            tui.Stop();
        }

        Assert.True(shown > 10);
        Assert.Empty(errors);
    }
}

/// <summary>对应审计复现的 <c>loader</c> 模式：SetIndicator 换到空帧表时，已派发的 tick 曾对 0 取模。</summary>
public class LoaderConcurrencyTests
{
    [Fact]
    public void SwappingToEmptyFramesWhileAnimatingNeverDividesByZero()
    {
        // DivideByZeroException 发生在计时器线程上，会直接杀死测试进程——跑到这里不崩就是通过。
        var tui = new FakeTui();
        var loaders = Enumerable.Range(0, 4)
            .Select(_ => new Loader(tui, s => s, s => s, "Working", new LoaderIndicatorOptions { Frames = ["a", "b"], IntervalMs = 1 }))
            .ToArray();
        var deadline = Environment.TickCount64 + 1500;
        var random = new Random(7);
        var workers = loaders.Select(loader => new Thread(() =>
        {
            var local = new Random(random.Next());
            while (Environment.TickCount64 < deadline)
            {
                loader.SetIndicator(new LoaderIndicatorOptions { Frames = ["a", "b", "c"], IntervalMs = 1 });
                Thread.SpinWait(local.Next(20000));
                loader.SetIndicator(new LoaderIndicatorOptions { Frames = [], IntervalMs = 1 });
            }
        })).ToArray();

        foreach (var worker in workers)
        {
            worker.Start();
        }
        foreach (var worker in workers)
        {
            worker.Join();
        }
        foreach (var loader in loaders)
        {
            loader.Stop();
            Assert.Equal("Working", loader.Render(40)[1].Trim());
        }
    }
}

/// <summary>无上界的数字捕获必须用 TryParse：溢出 = 不匹配，而不是在输入线程上抛 <see cref="OverflowException"/>。</summary>
public class NumericCaptureOverflowTests
{
    [Fact]
    public void OverlongCsiParametersAreNotKeys()
    {
        Assert.Null(Keys.ParseKey("\x1b[99999999999u"));
        Assert.False(Keys.MatchesKey("\x1b[99999999999u", "ctrl+c"));
        Assert.False(Keys.MatchesKey("\x1b[1;99999999999A", "up"));
        Assert.False(Keys.MatchesKey("\x1b[3;99999999999~", "delete"));
        Assert.False(Keys.MatchesKey("\x1b[1;99999999999H", "home"));
        Assert.Null(Keys.DecodePrintableKey("\x1b[97;99999999999u"));
        Assert.Null(Keys.DecodeKittyPrintable("\x1b[97:99999999999;2u"));
        Assert.Null(Keys.ParseKey("\x1b[27;5;99999999999~"));
        Assert.False(Keys.MatchesKey("\x1b[27;99999999999;99~", "ctrl+c"));

        // 正常长度不受影响。
        Assert.True(Keys.MatchesKey("\x1b[99;5u", "ctrl+c"));
        Assert.Equal("ctrl+c", Keys.ParseKey("\x1b[27;5;99~"));
    }

    [Theory]
    [InlineData("rgb:ffffffffff/0000000000/0000000000", 255.0, 0.0, 0.0)] // 10 位：过去 Convert.ToInt32 溢出
    [InlineData("rgb:ffffffff/00000000/00000000", 255.0, 0.0, 0.0)] // 恰好 8 位：过去变成负数
    [InlineData("rgb:80000000/00000000/ffffffff", 128.0, 0.0, 255.0)]
    [InlineData("rgb:ffff/0000/8000", 255.0, 0.0, 128.0)]
    public void OverlongOscHexChannelsScaleLikeParseInt(string color, double r, double g, double b)
    {
        var response = TerminalColors.ParseOscColorResponse($"\x1b]11;{color}\x07");

        Assert.NotNull(response);
        Assert.Equal(new RgbColor(r, g, b), response!.Value.Rgb);
    }

    [Fact]
    public void PastedTextWithOverlongNumbersDoesNotCrashTheEditor()
    {
        var tui = new TuiMainScreen(new StringTerminal(40, 20));
        var editor = new Editor(tui, new EditorTheme { BorderColor = s => s, SelectList = SelectListTheme.Plain });
        editor.Focused = true;

        // 终端把粘贴里的控制字节重编码成 CSI-u：解析不了的码点原样保留。
        editor.HandleInput("\x1b[200~a\x1b[99999999999;5ub\x1b[201~");
        Assert.NotEmpty(editor.Render(40));

        // 文本里的粘贴标记 id 来自用户、长度无上界。
        editor.SetText("[paste #99999999999] tail");
        Assert.NotEmpty(editor.Render(40));
        editor.HandleInput("\x1b[F"); // End
        editor.HandleInput("\x7f");
        Assert.NotEmpty(editor.Render(40));
    }
}

/// <summary>输入监听器的触发顺序要等于 JS <c>Set</c> 的插入顺序。</summary>
public class InputListenerOrderTests
{
    [Fact]
    public void ListenersFireInInsertionOrderAfterARemoval()
    {
        var terminal = new StringTerminal(80, 24);
        var tui = new TuiMainScreen(terminal);
        var order = new List<string>();
        Func<string, TuiInputListenerResult?> Listener(string name) => _ =>
        {
            order.Add(name);
            return null;
        };
        var a = Listener("A");
        var b = Listener("B");
        var c = Listener("C");
        var d = Listener("D");
        tui.AddInputListener(a);
        tui.AddInputListener(b);
        tui.AddInputListener(c);
        tui.RemoveInputListener(a);
        tui.AddInputListener(d);
        // Set.add 对已存在的成员是空操作，位置不变。
        tui.AddInputListener(b);
        tui.Start();
        try
        {
            terminal.SendInput("x");
        }
        finally
        {
            tui.Stop();
        }

        Assert.Equal(["B", "C", "D"], order);
    }
}

/// <summary>重定向 stdin 的读取按 UTF-16 码元转发，代理对的两半不再让读取任务崩溃。</summary>
public class RedirectedInputTests
{
    [Fact]
    public void SurrogatePairsArriveAsTwoHalvesInsteadOfFaultingTheReader()
    {
        var received = new List<string>();

        ProcessTerminal.PumpRedirectedInput(new StringReader("a😀b"), received.Add, CancellationToken.None);

        Assert.Equal(["a", "\ud83d", "\ude00", "b"], received);
        Assert.Equal("a😀b", string.Concat(received));
    }

    [Fact]
    public void CancellationStopsThePump()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var received = new List<string>();

        ProcessTerminal.PumpRedirectedInput(new StringReader("abc"), received.Add, cts.Token);

        Assert.Empty(received);
    }
}
