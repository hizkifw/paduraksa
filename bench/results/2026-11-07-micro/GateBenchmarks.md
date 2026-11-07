```

BenchmarkDotNet v0.15.8, Linux CachyOS
13th Gen Intel Core i5-13600K 0.80GHz, 1 CPU, 20 logical and 14 physical cores
.NET SDK 10.0.113
  [Host] : .NET 10.0.13 (10.0.13, 42.42.42.42424), X64 RyuJIT x86-64-v3

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
LaunchCount=1  WarmupCount=3  

```
| Method                  | Mean     | Error     | StdDev  | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------ |---------:|----------:|--------:|------:|--------:|----------:|------------:|
| Forward                 | 191.2 ns |  27.22 ns | 1.49 ns |  1.00 |    0.01 |         - |          NA |
| ForwardCloudflare       | 282.3 ns |   2.62 ns | 0.14 ns |  1.48 |    0.01 |         - |          NA |
| ForwardNonCanonicalPath | 222.2 ns |  11.19 ns | 0.61 ns |  1.16 |    0.01 |         - |          NA |
| AnsweredRespondHost     | 137.7 ns |   7.82 ns | 0.43 ns |  0.72 |    0.01 |         - |          NA |
| AnsweredRedirectHost    | 142.4 ns | 139.87 ns | 7.67 ns |  0.74 |    0.04 |         - |          NA |
| HttpsRedirect           | 140.5 ns |   3.27 ns | 0.18 ns |  0.73 |    0.01 |         - |          NA |
| UnknownHost             | 130.2 ns |   4.44 ns | 0.24 ns |  0.68 |    0.00 |         - |          NA |
