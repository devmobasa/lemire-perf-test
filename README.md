# How many strings can you create per second? (local rerun + C#)

Daniel Lemire asked how fast each language can make short new strings, in
[How many strings can you create per second?](https://lemire.me/blog/2026/09/25/how-many-strings-can-you-create-per-second/)
(25 September 2026). His test converts an integer to a string and stores it in a
1024-slot ring buffer, so the runtime cannot throw the work away. He ran Python,
Node.js, Bun, C++, Rust, Go and Nim on an Apple M4 Max.

This repository reruns his code unchanged on a 16-core processor (Linux) and
adds **C#** on **.NET 10 and .NET 11**, each with the JIT and with NativeAOT.

<!-- RESULTS:BEGIN -->
## Results

16-core processor, Linux 7.2.3-arch1-3, `performance` governor, boost on, pinning: none. Benchmarks ran one at a time in 3 rounds; each round ran every benchmark once, starting at a different point in the list. Every run reports Lemire's best of 5 passes; the tables show the median run. Raw output: [`results/2026-09-25_190428/`](results/2026-09-25_190428/).

| Implementation | Toolchain | M strings/s | ns/string | Runs (ns) | M4 Max, Lemire (M/s) |
|---|---|---:|---:|---|---:|
| C++ `std::to_string(i)` | GCC 16.2.1, -O3 | 159.3 | 6.28 | 6.28 / 6.23 / 6.28 | 183.8 |
| C# `i.ToString()` (ulong) | .NET 11.0.0-rc.1 NativeAOT | 158.8 | 6.30 | 6.30 / 6.31 / 6.30 | — |
| C# `i.ToString()` (ulong) | .NET 10.0.12 NativeAOT | 158.2 | 6.32 | 6.57 / 6.32 / 6.31 | — |
| C# `i.ToString()` (ulong) | .NET 11.0.0-rc.1 JIT | 152.6 | 6.55 | 6.63 / 6.55 / 6.55 | — |
| C# `i.ToString()` (ulong) | .NET 10.0.12 JIT | 149.9 | 6.67 | 6.70 / 6.67 / 6.67 | — |
| Rust `itoa + to_owned()` | rustc 1.98.1, release, itoa 1.0.18 | 111.6 | 8.96 | 8.96 / 8.96 / 8.94 | 71.0 |
| C++ `std::to_string(i)` | Clang 22.1.8, -O3 | 106.2 | 9.42 | 9.43 / 9.42 / 9.37 | 183.8 |
| Rust `i.to_string()` | rustc 1.98.1, release | 98.3 | 10.17 | 10.17 / 10.19 / 10.15 | 63.6 |
| Nim `$i` | Nim 2.2.12, -d:danger | 85.2 | 11.73 | 11.75 / 11.73 / 11.39 | 85.9 |
| Go `strconv.Itoa(i)` | Go 1.27.0 | 83.0 | 12.05 | 14.11 / 12.05 / 12.04 | 84.3 |
| Node.js `String(i)` | Node 25.9.0 | 52.2 | 19.14 | 19.14 / 19.11 / 19.33 | 73.1 |
| Bun `String(i)` | Bun 1.3.14 | 51.8 | 19.29 | 19.29 / 19.29 / 19.14 | 68.5 |
| Python `str(i)` | CPython 3.14.7 | 17.5 | 57.20 | 57.14 / 57.20 / 57.67 | 22.9 |

C# uses a `ulong` counter here, like the `uint64_t` and `u64` loops in C++ and Rust. Python converts 10 million integers (up to 7 digits) instead of 100 million, as in Lemire's code. The M4 Max column is from Lemire's post: a different machine, compare loosely. His C++ used Apple clang with libc++; both C++ builds here use libstdc++.

### C# with an `int` counter

An idiomatic C# variant. It changes both the loop arithmetic and the formatting path (`int.ToString()`), so compare it with the C# `ulong` rows, not with the other languages.

| Implementation | Toolchain | M strings/s | ns/string | Runs (ns) | M4 Max, Lemire (M/s) |
|---|---|---:|---:|---|---:|
| C# `i.ToString()` (int) | .NET 11.0.0-rc.1 NativeAOT | 174.2 | 5.74 | 5.73 / 5.74 / 5.74 | — |
| C# `i.ToString()` (int) | .NET 10.0.12 NativeAOT | 169.9 | 5.88 | 5.88 / 5.93 / 5.87 | — |
| C# `i.ToString()` (int) | .NET 11.0.0-rc.1 JIT | 168.0 | 5.95 | 5.98 / 5.95 / 5.95 | — |
| C# `i.ToString()` (int) | .NET 10.0.12 JIT | 166.1 | 6.02 | 6.02 / 6.01 / 6.03 | — |

<details>
<summary>Side cases: loop, empty string, and concatenation</summary>

Baselines and a short concatenation, from the JavaScript, Python and C# files.

| Case | Implementation | ns/string | M/s |
|---|---|---:|---:|
| `""` | Node.js, Node 25.9.0 | 0.35 | 2888.3 |
| `""` | C#, .NET 11.0.0-rc.1 JIT | 0.37 | 2715.4 |
| `""` | C#, .NET 11.0.0-rc.1 NativeAOT | 0.37 | 2713.0 |
| `""` | C#, .NET 10.0.12 JIT | 0.37 | 2689.2 |
| `""` | C#, .NET 10.0.12 NativeAOT | 0.37 | 2677.8 |
| `""` | Bun, Bun 1.3.14 | 0.37 | 2683.3 |
| `""` | Python, CPython 3.14.7 | 24.30 | 41.2 |
| `String()` | Bun, Bun 1.3.14 | 0.37 | 2687.5 |
| `String()` | Node.js, Node 25.9.0 | 2.93 | 340.8 |
| `loop only` | C#, .NET 11.0.0-rc.1 JIT | 0.29 | 3493.8 |
| `loop only` | Node.js, Node 25.9.0 | 0.35 | 2880.1 |
| `loop only` | C#, .NET 11.0.0-rc.1 NativeAOT | 0.37 | 2716.7 |
| `loop only` | C#, .NET 10.0.12 JIT | 0.37 | 2692.0 |
| `loop only` | C#, .NET 10.0.12 NativeAOT | 0.37 | 2702.9 |
| `loop only` | Bun, Bun 1.3.14 | 0.37 | 2717.6 |
| `loop only` | Python, CPython 3.14.7 | 24.37 | 41.0 |
| `p[i&15] + "x"` | Bun, Bun 1.3.14 | 2.13 | 470.0 |
| `p[i&15] + "x"` | C#, .NET 10.0.12 JIT | 5.73 | 174.5 |
| `p[i&15] + "x"` | C#, .NET 11.0.0-rc.1 JIT | 5.78 | 173.0 |
| `p[i&15] + "x"` | Node.js, Node 25.9.0 | 6.43 | 155.4 |
| `p[i&15] + "x"` | C#, .NET 10.0.12 NativeAOT | 8.21 | 121.8 |
| `p[i&15] + "x"` | C#, .NET 11.0.0-rc.1 NativeAOT | 8.29 | 120.7 |
| `p[i&15] + "x"` | Python, CPython 3.14.7 | 52.79 | 18.9 |
| `str()` | Python, CPython 3.14.7 | 31.33 | 31.9 |

</details>
<!-- RESULTS:END -->

## Reading the results

C# NativeAOT and GCC C++ are close on this workload; the C# JIT builds are a little
slower. These results cover sequential non-negative integers formatted into short,
mostly short-lived strings. Small differences across three runs are not enough to
establish a general performance advantage.

The [C# port](csharp/Program.cs) uses one million warm-up iterations, five measured
passes of 100 million conversions, a 1024-slot ring, and non-inlined benchmark
methods. JIT and NativeAOT use matching runtime packs within each .NET version.
Only each run's fastest pass is recorded. The empty-string and loop side cases
do not measure new string allocations.

## Run it yourself

You need: the .NET 11 SDK (pinned in [`global.json`](global.json); it downloads the
.NET 10 and 11 runtime packs), GCC, Clang, Rust (cargo), Go, Nim, Node.js, Bun and
Python 3. The scripts are C#
[file-based apps](https://learn.microsoft.com/dotnet/core/sdk/file-based-apps),
so `dotnet` runs them directly.

```sh
dotnet scripts/run.cs                     # 3 rounds of every benchmark into results/<timestamp>/
dotnet scripts/run.cs 5                   # 5 rounds (the count must be odd)
dotnet scripts/summarize.cs               # refresh README.md and results/<timestamp>/summary.json
```

Point `run.cs` at other tool binaries with `CXX_GCC`, `CXX_CLANG`, `CARGO`, `RUSTC`,
`GO`, `NIM`, `DOTNET`, `NODE`, `BUN` or `PYTHON`, for example
`NIM=~/nim-2.2.12/bin/nim dotnet scripts/run.cs`.

On Linux, optional `PIN="taskset -c 8"` pins the benchmark and its GC/JIT helper
threads to one core. The published run used no pinning.

## Credit

The benchmark design and code in [`lemire/`](lemire/) are Daniel Lemire's work.
The included benchmark files and M4 results are unchanged from his
[source folder at commit `361c1f2`](https://github.com/lemire/Code-used-on-Daniel-Lemire-s-blog/tree/361c1f2ed0291573bcaccaab4ee2d5a96188fe5b/2026/09/24).
His article, images, and plotting scripts are not included.
