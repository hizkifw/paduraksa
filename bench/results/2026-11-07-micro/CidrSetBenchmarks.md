```

BenchmarkDotNet v0.15.8, Linux CachyOS
13th Gen Intel Core i5-13600K 0.80GHz, 1 CPU, 20 logical and 14 physical cores
.NET SDK 10.0.113
  [Host] : .NET 10.0.13 (10.0.13, 42.42.42.42424), X64 RyuJIT x86-64-v3

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
LaunchCount=1  WarmupCount=3  

```
| Method | Ranges | Mean      | Error      | StdDev    | Allocated |
|------- |------- |----------:|-----------:|----------:|----------:|
| **HitV4**  | **20**     |  **2.897 ns** |  **0.4479 ns** | **0.0246 ns** |         **-** |
| HitV6  | 20     |  5.036 ns | 26.6885 ns | 1.4629 ns |         - |
| MissV4 | 20     |  2.667 ns |  2.7556 ns | 0.1510 ns |         - |
| **HitV4**  | **2000**   |  **9.332 ns** | **11.2824 ns** | **0.6184 ns** |         **-** |
| HitV6  | 2000   | 10.440 ns |  0.1922 ns | 0.0105 ns |         - |
| MissV4 | 2000   |  8.002 ns | 20.8955 ns | 1.1454 ns |         - |
