using MemRelief.Core.Contracts;
using MemRelief.Core.Storage;

namespace MemRelief.Core.Tests.Storage;

/// <summary>
/// 进程说明手册装载与解析测试（#58）：fail-safe 语义（缺失/损坏/异常 → 空表不抛——仅说明退化，
/// 与 RulePackStore 保护性降级分立）+ Resolve 取值优先级（手册 &gt; FileDescription &gt; CompanyName &gt; null）。
/// </summary>
public class DescriptionStoreTests
{
    [Fact]
    public void 真实嵌入资源_基线装载非空且条目合法()
    {
        var entries = new DescriptionStore().Load();

        Assert.NotEmpty(entries);
        Assert.All(entries, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Match));
            Assert.False(string.IsNullOrWhiteSpace(e.Description));
        });
    }

    [Fact]
    public void 注入JSON_解析条目并规整空白()
    {
        var json = """
            [
              { "match": " wechat " , "description": "微信" },
              { "match": "qq", "description": "QQ" }
            ]
            """;
        var entries = new DescriptionStore(_ => json).Load();

        Assert.Equal(2, entries.Count);
        Assert.Equal("wechat", entries[0].Match);
        Assert.Equal("微信", entries[0].Description);
    }

    [Theory]
    [InlineData(null)]                                  // 资源缺失
    [InlineData("not a json array")]                    // 解析失败
    public void 缺失或损坏_返回空表不抛(string? json)
    {
        var entries = new DescriptionStore(_ => json).Load();
        Assert.Empty(entries);
    }

    [Fact]
    public void 资源读取异常_返回空表不抛()
    {
        var entries = new DescriptionStore(_ => throw new IOException("盘读失败")).Load();
        Assert.Empty(entries);
    }

    [Fact]
    public void 解析防御_null字段条目被逐条剔除_不击穿整本手册()
    {
        // #58 评审修复：漏写 key/null 字段只损失该条目，整本手册不退空
        var json = """
            [
              { "match": "wechat", "description": null },
              { "match": null, "description": "无匹配键" },
              { "match": "qq", "description": "QQ" }
            ]
            """;
        var entries = new DescriptionStore(_ => json).Load();

        var entry = Assert.Single(entries);
        Assert.Equal("qq", entry.Match);
    }

    [Fact]
    public void 解析防御_空白条目被过滤()
    {
        var json = """
            [
              { "match": "  ", "description": "空匹配" },
              { "match": "qq", "description": " " },
              { "match": "wechat", "description": "微信" }
            ]
            """;
        var entries = new DescriptionStore(_ => json).Load();
        var entry = Assert.Single(entries);
        Assert.Equal("wechat", entry.Match);
    }

    // —— Resolve 取值优先级 ——

    private static readonly ProcessDescriptionEntry[] Handbook =
        [new("wechat", "微信（聊天通讯）")];

    [Fact]
    public void 手册命中_优先于FileDescription与公司名()
    {
        var resolved = DescriptionStore.Resolve("WeChat.exe", @"C:\apps\WeChat.exe", "聊天软件", "腾讯", Handbook);
        Assert.Equal("微信（聊天通讯）", resolved);
    }

    [Fact]
    public void 手册未命中_FileDescription次优先()
    {
        var resolved = DescriptionStore.Resolve("foo.exe", @"C:\apps\foo.exe", "Foo 工具", "Foo 厂商", Handbook);
        Assert.Equal("Foo 工具", resolved);
    }

    [Fact]
    public void FileDescription缺失_公司名兜底()
    {
        var resolved = DescriptionStore.Resolve("foo.exe", @"C:\apps\foo.exe", null, "Foo 厂商", Handbook);
        Assert.Equal("Foo 厂商", resolved);
    }

    [Fact]
    public void 全部缺失_返回null()
    {
        Assert.Null(DescriptionStore.Resolve("foo.exe", @"C:\apps\foo.exe", null, null, Handbook));
        Assert.Null(DescriptionStore.Resolve(null, null, null, null, Handbook));
    }

    [Fact]
    public void 元数据空白视为缺失()
    {
        Assert.Equal("Foo 厂商", DescriptionStore.Resolve("foo.exe", null, "   ", "Foo 厂商", Handbook));
        Assert.Null(DescriptionStore.Resolve("foo.exe", null, "  ", "  ", Handbook));
    }

    [Fact]
    public void 手册按名或路径子串匹配_大小写不敏感()
    {
        // 路径命中（名称不含关键字）
        Assert.Equal("微信（聊天通讯）",
            DescriptionStore.Resolve("app.exe", @"D:\Program Files\WeChat\app.exe", null, null, Handbook));
        // 名称命中（大小写不敏感）
        Assert.Equal("微信（聊天通讯）",
            DescriptionStore.Resolve("WECHATSERVICE.EXE", null, null, null, Handbook));
        // 均未命中
        Assert.Null(DescriptionStore.Resolve("foo.exe", @"C:\foo\foo.exe", null, null, Handbook));
    }
}
