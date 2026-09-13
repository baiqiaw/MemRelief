using MemRelief.App.Hosting;

namespace MemRelief.App.Tests.Hosting;

/// <summary>
/// 重启参数模型测试（T-27，data-contracts §2 ③.s4 裁决①⑧）：解析（自动重扫标志+失败项清单）、
/// 32K 命令行上限降级（超限→不携带失败项、仅自动重扫）、未知参数容错、序列化 round-trip。
/// </summary>
public class RestartOptionsTests
{
    private static RestartFailedItem Item(string name, string? path) => new(name, path);

    // —— 解析：启动行为裁决⑧（正常启动不自动扫描；仅重启参数才自动扫） ——

    [Fact]
    public void 解析_无参数_非重启_不自动扫()
    {
        var options = RestartOptions.Parse(Array.Empty<string>());

        Assert.False(options.AutoRescan);
        Assert.Empty(options.FailedItems);
    }

    [Fact]
    public void 解析_仅重启标志_自动扫_清单空()
    {
        var options = RestartOptions.Parse([RestartOptions.RestartFlag]);

        Assert.True(options.AutoRescan);
        Assert.Empty(options.FailedItems);
    }

    [Fact]
    public void 解析_标志与失败项_按名称与可执行路径还原()
    {
        var source = new[] { Item("a.exe", @"C:\apps\a.exe"), Item("svc.exe", null) };
        var args = RestartOptions.Create(autoRescan: true, source, executablePath: @"C:\app\MemRelief.exe")
            .ToArguments();

        var options = RestartOptions.Parse(args);

        Assert.True(options.AutoRescan);
        Assert.Equal(2, options.FailedItems.Count);
        Assert.Equal("a.exe", options.FailedItems[0].Name);
        Assert.Equal(@"C:\apps\a.exe", options.FailedItems[0].ExecutablePath);
        Assert.Equal("svc.exe", options.FailedItems[1].Name);
        Assert.Null(options.FailedItems[1].ExecutablePath);
    }

    // —— 解析：容错与降级（裁决①：32K 超限降级为“不携带、仅自动重扫”；解析侧防御同口径） ——

    [Fact]
    public void 解析_失败项载荷损坏_降级为仅自动重扫_不抛()
    {
        var options = RestartOptions.Parse(
            [RestartOptions.RestartFlag, RestartOptions.FailedPrefix + "not-base64!!"]);

        Assert.True(options.AutoRescan);
        Assert.Empty(options.FailedItems);
    }

    [Fact]
    public void 解析_失败项JSON非数组_降级为仅自动重扫()
    {
        var payload = Convert.ToBase64String("""{"n":"a.exe"}"""u8.ToArray());
        var options = RestartOptions.Parse(
            [RestartOptions.RestartFlag, RestartOptions.FailedPrefix + payload]);

        Assert.True(options.AutoRescan);
        Assert.Empty(options.FailedItems);
    }

    [Fact]
    public void 解析_总长超32K上限_防御性降级为仅自动重扫()
    {
        // 产生侧已在 Create 收口；此处验证解析侧对超长命令行同样防御（不信任输入）
        var huge = new string('x', RestartOptions.CommandLineLimit);
        var options = RestartOptions.Parse(
            [RestartOptions.RestartFlag, RestartOptions.FailedPrefix + huge]);

        Assert.True(options.AutoRescan);
        Assert.Empty(options.FailedItems);
    }

    [Fact]
    public void 解析_未知参数_忽略不影响启动行为()
    {
        var options = RestartOptions.Parse(["--other-flag", "C:\\some\\file.txt"]);

        Assert.False(options.AutoRescan);
        Assert.Empty(options.FailedItems);
    }

    [Fact]
    public void 解析_仅失败项无重启标志_非重启()
    {
        // 失败项仅重启链路有意义：无标志视为普通启动（用户文件关联等无关参数不触发自动扫）
        var options = RestartOptions.Parse([RestartOptions.FailedPrefix + "aaaa"]);

        Assert.False(options.AutoRescan);
        Assert.Empty(options.FailedItems);
    }

    // —— 序列化：round-trip 与 32K 降级（裁决①，产生侧收口） ——

    [Fact]
    public void 序列化_含失败项_可完整还原()
    {
        var source = new[] { Item("测试进程.exe", @"C:\目录\测试进程.exe") };
        var options = RestartOptions.Create(true, source, executablePath: @"C:\app\MemRelief.exe");

        var parsed = RestartOptions.Parse(options.ToArguments());

        Assert.True(parsed.AutoRescan);
        Assert.Single(parsed.FailedItems);
        Assert.Equal("测试进程.exe", parsed.FailedItems[0].Name);
        Assert.Equal(@"C:\目录\测试进程.exe", parsed.FailedItems[0].ExecutablePath);
    }

    [Fact]
    public void 序列化_无失败项_不携带失败参数()
    {
        var options = RestartOptions.Create(true, [], executablePath: @"C:\app\MemRelief.exe");

        Assert.Equal([RestartOptions.RestartFlag], options.ToArguments());
    }

    [Fact]
    public void 创建_命令行总长超32K上限_降级为不携带失败项仅自动重扫()
    {
        // Windows 命令行 32767 字符上限：清单超限时丢弃清单保自动重扫（裁决①边界，T-16 产生侧收口）
        var hugePath = @"C:\" + new string('长', 200) + ".exe";
        var items = Enumerable.Range(0, 400).Select(i => Item($"p{i}.exe", hugePath)).ToList();

        var options = RestartOptions.Create(true, items, executablePath: @"C:\app\MemRelief.exe");

        Assert.True(options.AutoRescan);
        Assert.Empty(options.FailedItems);
    }

    [Fact]
    public void 创建_未超限_完整携带失败项()
    {
        var items = new[] { Item("a.exe", @"C:\a.exe") };

        var options = RestartOptions.Create(true, items, executablePath: @"C:\app\MemRelief.exe");

        Assert.Single(options.FailedItems);
    }

    [Fact]
    public void 创建_命令行长度估算_可驱动上限边界()
    {
        // 边界自证：估算长度=可执行路径+引号+参数串长，等于实测 args 拼接
        var items = new[] { Item("a.exe", @"C:\a.exe") };
        var exe = @"C:\app\MemRelief.exe";
        var options = RestartOptions.Create(true, items, executablePath: exe);
        var argsText = string.Join(" ", options.ToArguments());

        Assert.Equal($"\"{exe}\"".Length + 1 + argsText.Length,
            RestartOptions.EstimateCommandLineLength(exe, options));
        Assert.True(RestartOptions.EstimateCommandLineLength(exe, options) <= RestartOptions.CommandLineLimit);
    }
}
