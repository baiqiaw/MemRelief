using MemRelief.Core;

namespace MemRelief.Core.Tests;

// 产品元数据守卫：Name/Version 供应用标题/日志头/自检输出消费
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
