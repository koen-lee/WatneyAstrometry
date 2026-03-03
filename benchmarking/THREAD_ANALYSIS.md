# Watney Benchmark: Thread-Count & Runtime Analysis

Companion to `BENCHMARK_RESULTS.md`. Focuses on two hypotheses:
1. **IO contention**: .NET 10 suffers more than .NET 6 as solver thread count (t1→t12) increases
2. **qdb4 faster, diminishing returns on nearby**: qdb4 reduces solver time for blind; nearby is already so fast that star detection dominates

**Configurations**: `{blind|nearby}_{net6|net10}_{qdb3|qdb4}_{t1|t4|t12}.csv`
The `t` suffix is the number of threads used by the quad-search solver within a single image solve.
All other parameters identical; all 9 images solved at 100% success rate.

---

## 1. Blind Solve: Full Process Time vs Thread Count

Mean across all 45 rows (9 images × 5 sampling levels):

| Runtime / DB | t1 (s) | t4 (s) | t12 (s) | t1→t4 speedup | t4→t12 speedup |
|---|---:|---:|---:|---:|---:|
| .NET 6 / qdb3 | 4.746 | 2.345 | 2.119 | **2.02×** | 1.11× |
| .NET 10 / qdb3 | 2.041 | 1.641 | 1.612 | **1.24×** | 1.02× |
| .NET 10 / qdb4 | 1.851 | 1.425 | 1.308 | **1.30×** | 1.09× |

Same view for solver time only (stripping image read, star detection, quad formation):

| Runtime / DB | t1 (s) | t4 (s) | t12 (s) | t1→t4 speedup | t4→t12 speedup |
|---|---:|---:|---:|---:|---:|
| .NET 6 / qdb3 | 4.320 | 1.933 | 1.669 | **2.24×** | 1.16× |
| .NET 10 / qdb3 | 1.452 | 1.066 | 1.023 | **1.36×** | 1.04× |
| .NET 10 / qdb4 | 1.284 | 0.851 | 0.729 | **1.51×** | 1.17× |

**Observations:**
- .NET 6 scales well with threads: going from t1 to t4 cuts solver time by 2.24×, and t12 still improves over t4 by another 16%
- .NET 10 / qdb3 barely benefits beyond t4: t4→t12 gives only a **4%** improvement vs **16%** for .NET 6
- .NET 10 / qdb4 scales somewhat better than qdb3 (17% t4→t12), suggesting qdb4's access pattern is more thread-friendly

### By Sampling Level (net10 / qdb3)

| Threads | s=16 | s=8 | s=4 | s=2 | s=1 |
|---|---:|---:|---:|---:|---:|
| t1 | 2.044 | 1.775 | 1.776 | 2.261 | 2.349 |
| t4 | 1.571 | 1.505 | 1.526 | 1.813 | 1.792 |
| t12 | 1.608 | 1.393 | 1.433 | 1.740 | **1.886** |

Note that at s=1 (full resolution, most stars, hardest search), t12 is **slower than t4** (1.886 vs 1.792).
Compared to .NET 6 / qdb3 where t12 always beats t4 at every sampling level.

---

## 2. IO Contention: The Heart-Nebula Case

Heart-nebula is the hardest image (most stars, most quads searched). It gives the clearest signal.
Values below are **solver time only** (seconds):

### .NET 6 / qdb3 — thread scaling is smooth
| Threads | s=16 | s=8 | s=4 | s=2 | s=1 |
|---|---:|---:|---:|---:|---:|
| t1 | 42.73 | 3.78 | 5.72 | 9.89 | 12.20 |
| t4 | 4.71 | 1.90 | 2.81 | 5.37 | 7.10 |
| t12 | **3.81** | **1.85** | **2.59** | **4.27** | **5.03** |

t12 consistently beats t4 across all sampling levels.

### .NET 10 / qdb3 — regression at t12 for high-stress cases
| Threads | s=16 | s=8 | s=4 | s=2 | s=1 |
|---|---:|---:|---:|---:|---:|
| t1 | 3.14 | 1.73 | 2.12 | 3.00 | 3.26 |
| t4 | **2.29** | 1.58 | **1.56** | **2.01** | **2.02** |
| t12 | 2.66 ⬆ | **1.21** | 1.56 | 1.96 | 2.58 ⬆ |

At s=16 and s=1, going t4→t12 **increases** solver time by 16% and 28% respectively.
s=8 and s=4 improve or stay flat. The stress points (highest star count at each extreme) regress.

### .NET 10 / qdb4 — mostly improving
| Threads | s=16 | s=8 | s=4 | s=2 | s=1 |
|---|---:|---:|---:|---:|---:|
| t1 | 6.25\* | 1.85 | 2.04 | 2.68 | 2.72 |
| t4 | **1.16** | 1.37 | 1.51 | 1.80 | **1.77** |
| t12 | **1.00** | **1.16** | **1.45** | **1.65** | 1.49 |

\* qdb4 at t1 / s=16 is notably worse than qdb3 (6.25s vs 3.14s). This is the one hard case where single-threaded qdb4 is slower — see §4 for discussion.

**Conclusion on hypothesis 1**: Partially confirmed. .NET 10 starts threads faster/more aggressively, leading to simultaneous peak IO against the quad database. The effect is real but modest at the aggregate level (the thread-to-thread gap is only a few percent), and .NET 10 still wins overall due to its much better single-thread baseline. The contention manifests clearly only on the hardest image at the most demanding sampling levels.

---

## 3. Nearby Solve: Thread Count is Irrelevant

Nearby solves use a position hint so the solver converges in ~1 iteration. Thread count has no effect:

| Runtime / DB | t1 (s) | t4 (s) | t12 (s) |
|---|---:|---:|---:|
| .NET 6 / qdb3 | 0.546 | 0.555 | **0.545** |
| .NET 10 / qdb3 | 0.695 | 0.704 | 0.710 |
| .NET 10 / qdb4 | 0.685 | 0.698 | 0.721 |

Solver times per image: ~0.15 s for all configs (negligible). The breakdown for t1:

| Component | .NET 6 / qdb3 | .NET 10 / qdb3 | .NET 10 / qdb4 |
|---|---:|---:|---:|
| Star detection (avg, s) | 0.161 | 0.327 | 0.324 |
| Solver (avg, s) | 0.153 | 0.154 | 0.147 |
| Full process (avg, s) | 0.546 | 0.695 | 0.685 |

Star detection in .NET 10 is **~2× slower** than .NET 6. The solver itself is identical between runtimes. Because nearby solving is already fast, star detection becomes the limiting step and entirely accounts for the ~27% gap between the runtimes.

qdb4 vs qdb3 for nearby: **no meaningful difference** (solver ≈ 0.15 s, qdb version irrelevant).

**Conclusion on hypothesis 2 (nearby part)**: Confirmed. On nearby solves, star detection dominates (~50% of total time), and solver improvements (more threads, better qdb) are irrelevant. The practical recommendation is to minimize star detection overhead rather than tune the solver.

---

## 4. qdb4 vs qdb3: Blind Solve Speedup

Mean solver time across all 45 blind rows:

| Threads | qdb3 solver (s) | qdb4 solver (s) | qdb4 speedup |
|---|---:|---:|---:|
| t1 | 1.452 | 1.284 | 1.13× |
| t4 | 1.066 | 0.851 | 1.25× |
| t12 | 1.023 | 0.729 | 1.40× |

qdb4's advantage **grows with thread count**. At t12, qdb4 is 40% faster at solving. This is consistent with qdb4 having a more IO-friendly access pattern under concurrent load — higher thread counts expose qdb3's IO bottleneck more, while qdb4 saturates less.

The anomalous heart-nebula / s=16 / t1 case (qdb4=6.25s vs qdb3=3.14s) is the one outlier where qdb4 is **slower at single thread**. This may indicate qdb4 requires examining more quad candidates for this particular hard image at high downsampling, but those candidates are checked faster in parallel — hence the dramatic reversal at t4 (1.16 vs 2.29s).

**Conclusion on hypothesis 2 (qdb4 part)**: Partially confirmed. qdb4 does process blind solves faster on average, and the gap increases with thread count. However, it is not uniformly faster at single thread for the hardest images, suggesting qdb4 is optimized for parallel throughput rather than single-threaded lookup count.

---

## 5. .NET 10 Star Detection Overhead

The ~2× star detection slowdown in .NET 10 appears across all thread counts and is not IO-related (star detection reads pixel data already in memory):

| Config | t1 stardet (s) | t4 stardet (s) | t12 stardet (s) |
|---|---:|---:|---:|
| blind net6/qdb3 | 0.164 | 0.161 | 0.168 |
| blind net10/qdb3 | 0.340 | 0.335 | 0.339 |
| nearby net6/qdb3 | 0.161 | 0.161 | 0.158 |
| nearby net10/qdb3 | 0.327 | 0.335 | 0.354 |

The overhead is consistent across blind and nearby, and is unaffected by solver thread count. Likely causes: JIT code generation differences, GC pressure, or SIMD path selection between runtimes. For blind solves the solver dominates and masks this; for nearby it is the primary cost.

---

## 6. Summary

| Question | Finding |
|---|---|
| Does net10 suffer IO contention at high thread counts? | Yes, but mildly. Regression appears only for the hardest image at extreme sampling levels (t12 vs t4 worse by 16–28% for heart-nebula). Net6 scales cleanly to t12. |
| Does net10's thread contention matter in practice? | Not much — net10 at t1 already beats net6 at t12, so even imperfect scaling keeps net10 ahead overall. |
| Does qdb4 process quads faster? | Yes, ~13% at t1, up to ~40% at t12. The advantage grows with thread count, consistent with better concurrent IO. |
| Diminishing returns of qdb4 for nearby? | Confirmed. qdb4 gives zero benefit for nearby; star detection is the only thing that matters. |
| Why is net10 slower for nearby despite being faster for blind? | Star detection is ~2× slower in net10 (CPU-bound, not IO), and nearby solving is so fast that this overhead dominates. |
| Recommended config for blind | net10 / qdb4 / t4–t12 |
| Recommended config for nearby | net6 / qdb3 / t1 (or any; threads irrelevant) |
