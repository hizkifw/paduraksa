```

BenchmarkDotNet v0.15.8, Linux CachyOS
13th Gen Intel Core i5-13600K 0.80GHz, 1 CPU, 20 logical and 14 physical cores
.NET SDK 10.0.113
  [Host] : .NET 10.0.13 (10.0.13, 42.42.42.42424), X64 RyuJIT x86-64-v3

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
LaunchCount=1  WarmupCount=3  

```
| Method | ServerName       | Mean     | Error     | StdDev   | Allocated |
|------- |----------------- |---------:|----------:|---------:|----------:|
| **Find**   | **H123.TEST.**       | **15.73 ns** |  **2.309 ns** | **0.127 ns** |         **-** |
| **Find**   | **a.w7.test**        | **17.75 ns** |  **1.581 ns** | **0.087 ns** |         **-** |
| **Find**   | **bücher.test**      | **26.80 ns** |  **1.993 ns** | **0.109 ns** |         **-** |
| **Find**   | **h123.test**        | **17.19 ns** | **11.547 ns** | **0.633 ns** |         **-** |
| **Find**   | **nope.example.org** | **11.82 ns** |  **6.226 ns** | **0.341 ns** |         **-** |
