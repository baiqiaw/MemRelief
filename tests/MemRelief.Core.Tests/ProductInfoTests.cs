using MemRelief.Core;

namespace MemRelief.Core.Tests;

// 占位测试：仅证明覆盖率链路（采集→阈值判定）端到端可用；ProductInfo 为占位类，T-01 落地后复核是否保留
public class ProductInfoTests
{
    [Fact]
    public void Name_非空()
    {
        Assert.False(string.IsNullOrWhiteSpace(ProductInfo.Name));
    }

    [Fact]
    public void Version_符合主次版本格式()
    {
        Assert.Matches(@"^\d+(\.\d+){1,3}$", ProductInfo.Version);
    }
}
