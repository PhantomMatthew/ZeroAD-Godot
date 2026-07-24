using Xunit;

// SimSystem 是进程级静态(UnitMotion 等组件经它取当前 ComponentManager),
// xUnit 默认跨测试类并行会让并发测试互相抢静态 → 偶发污染。全套件串行。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
