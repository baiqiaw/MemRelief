namespace MemRelief.Core;

// 产品元数据（T-01 复核结论：保留，供应用标题/日志头/自检输出消费）
// 用属性而非 const：保证可插桩（覆盖率有真实数据点），且避免 const 内联的版本化问题
public static class ProductInfo
{
    public static string Name => "MemRelief";

    public static string Version => "0.1.0";
}
