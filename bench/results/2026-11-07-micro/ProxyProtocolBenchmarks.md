```

BenchmarkDotNet v0.15.8, Linux CachyOS
13th Gen Intel Core i5-13600K 0.80GHz, 1 CPU, 20 logical and 14 physical cores
.NET SDK 10.0.113
  [Host] : .NET 10.0.13 (10.0.13, 42.42.42.42424), X64 RyuJIT x86-64-v3

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
LaunchCount=1  WarmupCount=3  

```
| Method              | Mean       | Error      | StdDev    | Allocated |
|-------------------- |-----------:|-----------:|----------:|----------:|
| V1Tcp4              |  44.170 ns |   6.396 ns | 0.3506 ns |         - |
| V1Tcp6              | 116.771 ns | 156.228 ns | 8.5634 ns |         - |
| V2IPv4              |   8.870 ns |   1.073 ns | 0.0588 ns |         - |
| V2IPv6WithTlvs      |  18.886 ns |   4.320 ns | 0.2368 ns |         - |
| V2IPv6WithTlvsSplit |  34.788 ns |   1.061 ns | 0.0581 ns |         - |
