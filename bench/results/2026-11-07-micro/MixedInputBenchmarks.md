```

BenchmarkDotNet v0.15.8, Linux CachyOS
13th Gen Intel Core i5-13600K 0.80GHz, 1 CPU, 20 logical and 14 physical cores
.NET SDK 10.0.113
  [Host] : .NET 10.0.13 (10.0.13, 42.42.42.42424), X64 RyuJIT x86-64-v3

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
LaunchCount=1  WarmupCount=3  

```
| Method            | Mean     | Error     | StdDev   | Gen0   | Allocated |
|------------------ |---------:|----------:|---------:|-------:|----------:|
| HostTableFind     | 21.50 ns |  1.485 ns | 0.081 ns |      - |         - |
| IpParserChars     | 29.38 ns |  0.952 ns | 0.052 ns |      - |         - |
| BclIpAddressChars | 48.67 ns | 25.680 ns | 1.408 ns | 0.0047 |      60 B |
