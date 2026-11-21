using Xunit;

// 分配量断言（GC.GetTotalAllocatedBytes 为进程级统计）依赖测试之间的隔离，
// 关闭跨测试类并行，避免其它测试的大对象分配污染保存/加载分配量测量。
[assembly: CollectionBehavior(DisableTestParallelization = true)]