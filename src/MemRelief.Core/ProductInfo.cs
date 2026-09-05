namespace MemRelief.Core;

// 占位类：证明测试→覆盖率→阈值门禁链路端到端可用；T-01 落地后复核是否保留（issue #1 已登记）
// 用属性而非 const：保证可插桩（覆盖率有真实数据点），且避免 const 内联的版本化问题
public static class ProductInfo
{
    public static string Name => "MemRelief";

    public static string Version => "0.1.0";
}
