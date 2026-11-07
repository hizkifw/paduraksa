```

BenchmarkDotNet v0.15.8, Linux CachyOS
13th Gen Intel Core i5-13600K 0.80GHz, 1 CPU, 20 logical and 14 physical cores
.NET SDK 10.0.113
  [Host] : .NET 10.0.13 (10.0.13, 42.42.42.42424), X64 RyuJIT x86-64-v3

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
LaunchCount=1  WarmupCount=3  

```
| Method      | Path                  | Mean      | Error      | StdDev    | Allocated |
|------------ |---------------------- |----------:|-----------:|----------:|----------:|
| **IsCanonical** | **/api/v1/items/42**      | **14.138 ns** | **14.0653 ns** | **0.7710 ns** |         **-** |
| Pick        | /api/v1/items/42      | 18.048 ns | 29.3158 ns | 1.6069 ns |         - |
| **IsCanonical** | **/api\\(...)nid=x [32]** |  **3.172 ns** |  **0.8889 ns** | **0.0487 ns** |         **-** |
| Pick        | /api\\(...)nid=x [32] | 61.332 ns | 25.2316 ns | 1.3830 ns |         - |
| **IsCanonical** | **/asse(...)1c.js [21]**  |  **6.355 ns** |  **0.0902 ns** | **0.0049 ns** |         **-** |
| Pick        | /asse(...)1c.js [21]  | 27.417 ns |  0.2867 ns | 0.0157 ns |         - |
| **IsCanonical** | **/dashboard/settings**   |  **6.496 ns** |  **1.2588 ns** | **0.0690 ns** |         **-** |
| Pick        | /dashboard/settings   | 29.475 ns |  0.3248 ns | 0.0178 ns |         - |
