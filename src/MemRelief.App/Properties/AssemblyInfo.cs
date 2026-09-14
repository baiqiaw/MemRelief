using System.Runtime.CompilerServices;

// 测试可达性：App.Tests 覆盖 internal 组件（如 Text/DisplayText 展示文案）；
// 程序集未签名，友元声明按简单名匹配
[assembly: InternalsVisibleTo("MemRelief.App.Tests")]
