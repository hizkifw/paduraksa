```

BenchmarkDotNet v0.15.8, Linux CachyOS
13th Gen Intel Core i5-13600K 0.80GHz, 1 CPU, 20 logical and 14 physical cores
.NET SDK 10.0.113
  [Host] : .NET 10.0.13 (10.0.13, 42.42.42.42424), X64 RyuJIT x86-64-v3

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
LaunchCount=1  WarmupCount=3  

```
| Method                       | Mean      | Error      | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|----------------------------- |----------:|-----------:|----------:|------:|--------:|----------:|------------:|
| Off                          |  5.654 ns |  1.8308 ns | 0.1004 ns |  1.00 |    0.02 |         - |          NA |
| GenericUntrustedPeer         |  5.855 ns |  0.0829 ns | 0.0045 ns |  1.04 |    0.02 |         - |          NA |
| GenericForwardedFor          | 63.483 ns | 10.4226 ns | 0.5713 ns | 11.23 |    0.19 |         - |          NA |
| CloudflareProxyV4            | 34.986 ns |  0.4732 ns | 0.0259 ns |  6.19 |    0.09 |         - |          NA |
| CloudflareProxyV6            | 73.626 ns | 56.7170 ns | 3.1089 ns | 13.03 |    0.52 |         - |          NA |
| CloudflareProxyMissingHeader | 18.453 ns |  0.2673 ns | 0.0147 ns |  3.26 |    0.05 |         - |          NA |
| CloudflareTunnelV4           | 33.021 ns | 49.7526 ns | 2.7271 ns |  5.84 |    0.43 |         - |          NA |
