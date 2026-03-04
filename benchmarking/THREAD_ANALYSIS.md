# Watney Benchmark: Thread-Count & Runtime Analysis

Companion to `BENCHMARK_RESULTS.md`. Focuses on two hypotheses:
1. **IO contention**: .NET 10 suffers more than .NET 6 as solver thread count (t1→t12) increases
2. **qdb4 faster, diminishing returns on nearby**: qdb4 reduces solver time for blind; nearby is already so fast that star detection dominates

**Configurations**: `{blind|nearby}_{net6|net10}_{qdb3|qdb4}_{t1|t4|t12}.csv`
The `t` suffix is the number of threads used by the quad-search solver within a single image solve.
Runs include a warmup solve (first image, first sampling, discarded) to avoid cold OS file-cache effects.
All other parameters identical; 100% solve success across all configurations.

---

## 1. Blind Solve: Full Process Time vs Thread Count

Mean across all 45 rows (9 images × 5 sampling levels):

| Runtime / DB | t1 (s) | t4 (s) | t12 (s) | t1→t4 | t4→t12 | t1→t12 |
|---|---:|---:|---:|---:|---:|---:|
| .NET 6 / qdb3  | 4.013 | 2.305 | 1.976 | 1.74× | 1.17× | 2.03× |
| .NET 10 / qdb3 | 2.476 | 1.683 | 1.591 | 1.47× | 1.06× | 1.56× |
| .NET 10 / qdb4 | 1.918 | 1.442 | 1.289 | 1.33× | 1.12× | 1.49× |

Solver time only (stripping image read, star detection, quad formation overhead):

| Runtime / DB | t1 (s) | t4 (s) | t12 (s) | t1→t4 | t4→t12 | t1→t12 |
|---|---:|---:|---:|---:|---:|---:|
| .NET 6 / qdb3  | 3.601 | 1.894 | 1.557 | 1.90× | 1.22× | 2.31× |
| .NET 10 / qdb3 | 1.794 | 1.098 | 0.996 | 1.63× | 1.10× | 1.80× |
| .NET 10 / qdb4 | 1.303 | 0.855 | 0.707 | 1.52× | 1.21× | 1.84× |

By sampling level — full process mean:

| Threads | s=16 | s=8 | s=4 | s=2 | s=1 |
|---|---:|---:|---:|---:|---:|
| **net6 / qdb3** |||||
| t1  | 3.726 | 2.634 | 2.987 | 5.059 | 5.660 |
| t4  | 2.218 | 1.690 | 1.835 | 2.601 | 3.181 |
| t12 | 1.811 | 1.555 | 1.652 | 2.231 | 2.631 |
| **net10 / qdb3** |||||
| t1  | 1.930 | 2.032 | 2.640 | 3.148 | 2.628 |
| t4  | 1.597 | 1.549 | 1.614 | 1.793 | 1.863 |
| t12 | 1.494 | 1.316 | 1.465 | 1.812 | 1.866 |
| **net10 / qdb4** |||||
| t1  | 1.734 | 1.702 | 1.876 | 2.089 | 2.189 |
| t4  | 1.253 | 1.312 | 1.463 | 1.590 | 1.594 |
| t12 | 1.129 | 1.188 | 1.298 | 1.457 | 1.373 |

**Observations:**
- .NET 6 scales well across all sampling levels; t4→t12 consistently helps
- .NET 10 / qdb3 also scales smoothly after warmup — no clear regression at t12
- .NET 10's advantage over .NET 6 narrows as thread count rises (1.62× at t1, 1.24× at t12), indicating the runtime's single-threaded solver efficiency is its main edge

---

## 2. IO Contention Hypothesis: Assessment

The earlier (pre-warmup) data showed heart-nebula regressing at t12 vs t4 for net10.
With warmup, the picture is cleaner:

### Heart-Nebula solver time (hardest image)

**.NET 6 / qdb3** — smooth scaling:
| Threads | s=16 | s=8 | s=4 | s=2 | s=1 |
|---|---:|---:|---:|---:|---:|
| t1  | 8.158 | 3.655 | 5.689 | 11.642 | 12.891 |
| t4  | 4.363 | 2.010 | 2.826 |  4.927 |  6.062 |
| t12 | **3.596** | **1.711** | **2.452** | **3.942** | **4.844** |

t12 always beats t4; scaling is consistent.

**.NET 10 / qdb3** — flat t4→t12, no regression:
| Threads | s=16 | s=8 | s=4 | s=2 | s=1 |
|---|---:|---:|---:|---:|---:|
| t1  | 3.051 | 1.856 | 3.171 | 6.235 | 3.967 |
| t4  | 2.363 | 1.554 | 1.872 | 2.288 | 2.212 |
| t12 | **2.337** | **1.103** | **1.568** | **2.206** | **2.188** |

t12 is marginally better than or equal to t4; no meaningful regression after warmup.

**.NET 10 / qdb4** — consistent improvement:
| Threads | s=16 | s=8 | s=4 | s=2 | s=1 |
|---|---:|---:|---:|---:|---:|
| t1  | 1.862 | 1.903 | 2.218 | 2.809 | 2.980 |
| t4  | 1.121 | 1.410 | 1.676 | 1.765 | 1.789 |
| t12 | **0.903** | **1.016** | **1.210** | **1.677** | **1.423** |

**Revised conclusion on Hypothesis 1:** The regression seen before was a cold-cache artifact, not a systematic .NET 10 problem. With a warm OS file cache, both runtimes scale cleanly with thread count. .NET 10 does plateau faster than .NET 6 (its t4→t12 gain is ~6–10% vs ~16–22% for .NET 6), which is consistent with already-efficient single-threaded code leaving less room for parallelism — not with IO contention causing harm.

---

## 3. qdb4 vs qdb3: Blind Solve

Mean solver time across all 45 rows (net10 only):

| Threads | qdb3 solver (s) | qdb4 solver (s) | qdb4 speedup |
|---|---:|---:|---:|
| t1  | 1.794 | 1.303 | **1.38×** |
| t4  | 1.098 | 0.855 | **1.28×** |
| t12 | 0.996 | 0.707 | **1.41×** |

qdb4 is faster at every thread count. The advantage is fairly consistent (~28–41%), showing qdb4 genuinely processes quads faster rather than only benefiting from parallelism.

The cold-cache anomaly from the previous run (qdb4 / t1 / s=16 at 6.25s vs qdb3 at 3.14s) is gone after warmup — qdb4 t1/s=16 is now 1.86s vs qdb3 at 3.05s, as expected.

---

## 4. Nearby Solve: Star Detection Dominates

Thread count has no meaningful effect on nearby solves (solver time ≈ 0.15s regardless):

| Runtime / DB | t1 (s) | t4 (s) | t12 (s) |
|---|---:|---:|---:|
| .NET 6 / qdb3  | 0.596 | 0.537 | 0.542 |
| .NET 10 / qdb3 | 0.718 | 0.707 | 0.693 |
| .NET 10 / qdb4 | 0.683 | 0.680 | 0.694 |

Time breakdown for t1:

| Component | .NET 6 / qdb3 | .NET 10 / qdb3 | .NET 10 / qdb4 |
|---|---:|---:|---:|
| Star detection (avg, s) | 0.175 | 0.337 | 0.331 |
| Solver (avg, s)         | 0.167 | 0.161 | 0.145 |
| Full process (avg, s)   | 0.596 | 0.718 | 0.683 |

Star detection in .NET 10 is **~2× slower** than .NET 6 and accounts for the entire runtime gap. Per image:

| Image | net6 full (s) | n10 full (s) | net6 stardet (s) | n10 stardet (s) |
|---|---:|---:|---:|---:|
| heart-nebula | 0.793 | 0.993 | 0.152 | 0.285 |
| ic1795       | 0.703 | 0.820 | 0.265 | 0.462 |
| m31          | 0.566 | 0.720 | 0.240 | 0.424 |
| m33          | 0.440 | 0.547 | 0.078 | 0.259 |
| m81          | 0.511 | 0.513 | 0.025 | 0.098 |
| ngc1491      | 0.729 | 0.791 | 0.297 | 0.442 |
| ngc383       | 0.602 | 0.767 | 0.237 | 0.447 |
| ngc7331      | 0.424 | 0.532 | 0.041 | 0.170 |
| ngc925       | 0.592 | 0.782 | 0.242 | 0.449 |

m81 and ngc7331 (small/sparse images, fast star detection) are near-equal between runtimes; large FITS images with many stars show the full 2× gap.

qdb4 vs qdb3 for nearby: no difference — solver is already sub-0.17s, so quad database version is irrelevant.

**Conclusion on Hypothesis 2:** Confirmed. For nearby solves, the solver (including quad database version and thread count) is irrelevant; star detection is the bottleneck and runs ~2× slower on .NET 10. For blind solves, qdb4 gives a genuine ~35% solver speedup that is present at every thread count.

---

## 5. .NET 10 Star Detection Overhead

The overhead is CPU-bound and consistent across blind and nearby, independent of thread count:

| Config | blind stardet (s) | nearby stardet (s) |
|---|---:|---:|
| net6 / qdb3  | 0.162 | 0.164 |
| net10 / qdb3 | 0.351 | 0.344 |
| net10 / qdb4 | 0.342 | 0.340 |

The overhead does not grow with thread count, ruling out lock contention. Likely cause: JIT code-generation differences in the pixel-processing path (SIMD usage, loop vectorization, or GC pressure during image scanning).

---

## 6. Summary

| Question | Finding |
|---|---|
| Does net10 suffer IO contention at high thread counts? | **No** — the t12 regression seen in cold-cache data was an artifact. With warmup, both runtimes scale smoothly. |
| Does net10 scale as well as net6 with threads? | Slightly less (1.56× t1→t12 vs 2.03×), but this reflects its better single-threaded baseline, not contention. |
| Does qdb4 process quads faster? | **Yes** — ~35% faster solver time at all thread counts; no longer anomalously slow at t1/s=16 with warm caches. |
| Diminishing returns of qdb4 for nearby? | **Confirmed.** Solver time is negligible for nearby; qdb version makes no difference. |
| Why is net10 slower for nearby despite faster blind? | Star detection is ~2× slower on net10 (CPU-bound). For blind, this is masked by solver time; for nearby it's the dominant cost. |
| Recommended config for blind | **net10 / qdb4 / t12** |
| Recommended config for nearby | **net6 / qdb3** (any thread count; threads irrelevant) |
