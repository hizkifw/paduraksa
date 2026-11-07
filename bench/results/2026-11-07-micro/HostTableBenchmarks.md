```

BenchmarkDotNet v0.15.8, Linux CachyOS
13th Gen Intel Core i5-13600K 0.80GHz, 1 CPU, 20 logical and 14 physical cores
.NET SDK 10.0.113
  [Host] : .NET 10.0.13 (10.0.13, 42.42.42.42424), X64 RyuJIT x86-64-v3

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
LaunchCount=1  WarmupCount=3  

```
| Method | Host             | Mean     | Error     | StdDev   | Allocated |
|------- |----------------- |---------:|----------:|---------:|----------:|
| **Find**   | **H123.TEST**        | **19.18 ns** |  **3.778 ns** | **0.207 ns** |         **-** |
| **Find**   | **a.w7.test**        | **26.69 ns** | **18.640 ns** | **1.022 ns** |         **-** |
| **Find**   | **h123.test**        | **18.93 ns** |  **1.838 ns** | **0.101 ns** |         **-** |
| **Find**   | **h123.test:8443**   | **18.72 ns** |  **3.887 ns** | **0.213 ns** |         **-** |
| **Find**   | **nope.example.org** | **16.88 ns** |  **0.512 ns** | **0.028 ns** |         **-** |
