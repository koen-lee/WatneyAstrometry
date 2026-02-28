# Plan: Quad Database v4 — SoA Layout + R0-Sorted Quads

## Goal

Convert the existing v3 `.qdb` files to a new v4 format that:
1. Sorts quads within each subcell by R0 (ascending)
2. Splits each subcell's data into a ratio section followed by a float (metadata) section
   — "Structure of Arrays" instead of "Array of Structures"

Then update the Core reader to exploit both properties via a merge-join loop,
replacing the current per-DB-quad binary search.

---

## Why

### Current (v3) AoS layout per subcell:
```
[r0..r4|ld|ra|dec][r0..r4|ld|ra|dec]...   — 18 bytes/quad
 <-- 6B -->< 12B >
```
- Quads are unordered (random deduplication order from `Distinct()`).
- Every quad's 12 float bytes are fetched even though matches are ~0.0005%.
- Binary search into the 330-element image-quad list: O(N_db × log M_img) per subcell.

### New (v4) SoA layout per subcell, quads sorted by R0:
```
[r0..r4][r0..r4]...[r0..r4]   N × 6 bytes — ratio section
[ld|ra|dec][ld|ra|dec]...      N × 12 bytes — float section
```
- Ratios for all N quads fit in 1/3 of the bytes → better cache utilisation.
- Sorted order enables a merge-join: one linear pass through both sorted lists,
  O(N_db + M_img) instead of O(N_db × log M_img).
- Float section accessed only on a match (≈once per subcell on average).

---

## Format specification (v4)

### File header (identical to v3):
```
"WATNEYQDB"  (9 bytes, ASCII)
4            (int32, format version — was 3)
```

### Index file: unchanged.
`DataLengthBytes` per subcell stays N × 18. The split point is derived:
```
ratioSectionEnd = dataStartPos + (DataLengthBytes / 18) * 6
```
No index schema change; old index files continue to work.

### Subcell data block (v4):
```
ratio_section:  N × 6 bytes  (packed 5 ratios, sorted ascending by R0)
float_section:  N × 12 bytes (LargestDistance float, Ra float, Dec float)
                              in the same order as ratio_section
```

---

## Part 1 — Converter (`WatneyAstrometry.QuadDbConverter`)

### New project
- Type: console app, net8.0 (no dependency on Core required; include
  the ratio decoding math inline or copy the tiny constants).
- Location: `src/WatneyAstrometry.QuadDbConverter/`

### CLI
```
QuadDbConverter --input <v3-db-dir> --output <v4-db-dir> [--parallel <n>]
```

### Algorithm (streaming, low memory)
```
For each .qdbindex file in input dir:
  Copy index file unchanged to output dir.

For each .qdb data file in input dir (can run Parallel.ForEach):
  Open input file for reading.
  Open output file for writing.

  Write header: "WATNEYQDB" + version=4.

  Read input header (9+4 = 13 bytes) to skip it.

  For each subcell (positions known from the index):
    quadCount = DataLengthBytes / 18
    Read quadCount × 18 bytes into a temp buffer.

    Decode R0 sort key for each quad:
      r0_raw = ((buf[q*18 + 1] & 0x03) << 8) | buf[q*18 + 0]
      (This is the 10-bit integer R0, range 0–1023. No float needed.)

    Sort quads by r0_raw (stable sort; in-place on the temp buffer or
    via an index array to avoid moving 18-byte blocks).

    Write ratio section:  for each sorted quad → buf[q*18 .. q*18+5]  (6 bytes)
    Write float section:  for each sorted quad → buf[q*18+6 .. q*18+17] (12 bytes)
```

R0 raw extraction from the 6-byte packed field:
```csharp
// bits [0..9] of the 48-bit packed ulong
int R0Raw(byte* p6) => ((p6[1] & 0x03) << 8) | p6[0];
```

Sort by index array (avoids moving 18-byte structs):
```csharp
int[] order = Enumerable.Range(0, quadCount).ToArray();
Array.Sort(order, (a, b) => R0Raw(buf + a*18).CompareTo(R0Raw(buf + b*18)));
```

### What the converter does NOT do
- Does not re-encode ratios or floats — bytes are copied verbatim.
- Does not modify the index file.
- Does not need a reference to `WatneyAstrometry.Core`.

---

## Part 2 — Reader changes (`WatneyAstrometry.Core`)

### `QuadDatabaseCellFile.cs`

#### 1. Capture file version after validation
```csharp
private int _fileFormatVersion = 0;
// in the existing version-check block:
_fileFormatVersion = versionNum; // store after validating 3 or 4
```

#### 2. Replace per-quad loop with subcell-level dispatch
In `GetQuads`, the inner loop currently calls `BytesToQuadNew` per DB quad.
For v4, replace it with a single call to `ProcessSubCellMergeJoin`:

```csharp
// v3 path (existing, unchanged):
if (_fileFormatVersion == 3)
{
    for (var q = startIndex; q < nextStartIndex; q++)
    {
        var quad = BytesToQuadNew(pSubCellDataBytes, advance, imageQuads, _bytesNeedReversing);
        // ... existing match/range logic ...
        advance += QuadDataLen;
    }
}
// v4 path (new):
else
{
    ProcessSubCellMergeJoin(
        pSubCellDataBytes, startIndex, nextStartIndex - startIndex,
        imageQuads, _bytesNeedReversing,
        center, angularDistance,
        matchingQuads, matchingQuadsWithinRange);
}
```

#### 3. New method: `ProcessSubCellMergeJoin`

```csharp
private static unsafe void ProcessSubCellMergeJoin(
    byte* pBuf,
    int startIndex,        // first DB quad index to process (subsetting)
    int count,             // number of DB quads in this subset
    ImageStarQuad[] imageQuads,
    bool bytesNeedReversing,
    EquatorialCoords center,
    double angularDistance,
    List<StarQuad> matchingQuads,
    List<StarQuad> matchingQuadsWithinRange)
{
    // v4 SoA layout within the subcell data block:
    // [0 .. totalQuads*6 - 1]        → ratio section (all N quads)
    // [totalQuads*6 .. totalQuads*18] → float section (all N quads)
    //
    // For a subsetted range [startIndex, startIndex+count), we read from:
    //   ratios: pBuf + startIndex * 6
    //   floats: pBuf + totalQuads * 6 + startIndex * 12
    //
    // totalQuads is not passed directly but can be inferred if needed; however,
    // the caller already has subCellsInRangeArr[sc].DataLengthBytes / QuadDataLen.
    // Pass totalQuads as an additional parameter (see caller update above).

    byte* pRatios = pBuf + startIndex * 6;
    byte* pFloats = pBuf + totalQuads * 6 + startIndex * 12;

    int imgJ = 0; // lower-bound pointer into imageQuads; only ever advances

    for (int dbIdx = 0; dbIdx < count; dbIdx++)
    {
        byte* pR = pRatios + dbIdx * 6;

        // Decode R0 only for the window check.
        float r0 = (((pR[1] << 8) & 0x3FF) + (pR[0] & 0x3FF)) * OnePer1023;
        float lo0 = r0 * RatioMatchLow;
        float hi0 = r0 * RatioMatchHigh;

        // Advance imgJ past image quads below this window (never rewinds).
        while (imgJ < imageQuads.Length && imageQuads[imgJ].Ratios.R0 < lo0)
            imgJ++;

        if (imgJ >= imageQuads.Length) break; // all image quads exhausted

        float imgR0atJ = imageQuads[imgJ].Ratios.R0;
        if (imgR0atJ > hi0) continue; // no image quad in window; next DB quad

        // R0 window has candidates — decode the remaining 4 ratios.
        float r1 = ((((pR[2] & 0x0F) << 6) & 0x3FF) + ((pR[1] >> 2) & 0x3FF)) * OnePer1023;
        float r2 = ((((pR[3] & 0x3F) << 4) & 0x3FF) + ((pR[2] >> 4) & 0x3FF)) * OnePer1023;
        float r3 = ((((pR[4] & 0x7F) << 2) & 0x1FF) + ((pR[3] >> 6) & 0x1FF)) * OnePer511;
        float r4 = ((((pR[5])        << 1) & 0x1FF) + ((pR[4] >> 7) & 0x1FF)) * OnePer511;

        var dbRatios = new QuadRatios(r0, r1, r2, r3, r4);
        var lo = dbRatios * RatioMatchLow;
        var hi = dbRatios * RatioMatchHigh;

        for (int k = imgJ; k < imageQuads.Length; k++)
        {
            var imgQuad = imageQuads[k];
            if (imgQuad.Ratios.R0 > hi0) break; // past R0 window

            if (imgQuad.Ratios.R1 < lo.R1 || imgQuad.Ratios.R1 > hi.R1) continue;
            if (imgQuad.Ratios.R2 < lo.R2 || imgQuad.Ratios.R2 > hi.R2) continue;
            if (imgQuad.Ratios.R3 < lo.R3 || imgQuad.Ratios.R3 > hi.R3) continue;
            if (imgQuad.Ratios.R4 < lo.R4 || imgQuad.Ratios.R4 > hi.R4) continue;

            // Full match — decode float section.
            byte* pF = pFloats + dbIdx * 12;
            float ld, ra, dec;
            if (bytesNeedReversing)
            {
                ld  = BitConverter.ToSingle(new byte[] { pF[3],  pF[2],  pF[1],  pF[0]  }, 0);
                ra  = BitConverter.ToSingle(new byte[] { pF[7],  pF[6],  pF[5],  pF[4]  }, 0);
                dec = BitConverter.ToSingle(new byte[] { pF[11], pF[10], pF[9],  pF[8]  }, 0);
            }
            else
            {
                var if1 = *(int*)pF; pF += sizeof(int);
                var if2 = *(int*)pF; pF += sizeof(int);
                var if3 = *(int*)pF;
                ld  = *(float*)&if1;
                ra  = *(float*)&if2;
                dec = *(float*)&if3;
            }

            var quad = new StarQuad(dbRatios, ld, new EquatorialCoords(ra, dec));
            matchingQuads.Add(quad);
            if (quad.MidPoint.GetAngularDistanceTo(center) < angularDistance)
                matchingQuadsWithinRange.Add(quad);
            break; // one DB quad → at most one image quad match
        }
    }
}
```

**Extra bonus of deferred R1-R4 decode:** R0 window is ~2.2% of [0,1]. Only
the ~2.2% of DB quads that have an image quad in their R0 window need the full
decode. The other ~97.8% are rejected after decoding only R0 (the outer
`continue`). This saves ~5 multiplies + bit shifts per quad.

#### 4. `LowerBound` helper
Can be kept for v3 compat but is no longer used in the v4 path.
After v3 is retired it can be removed.

#### 5. Sorting image quads
The `Array.Sort` added in `Solver.cs` after `FormImageStarQuads` is already
in place and required by both v3 (binary search) and v4 (merge join). No change.

---

## Open questions / decisions before implementing

1. **`totalQuads` parameter**: `ProcessSubCellMergeJoin` needs to know the
   total quad count in the subcell to compute the float section offset, even when
   processing a subset. Pass it as an int from the caller (it's
   `DataLengthBytes / QuadDataLen`).

2. **Backward compatibility**: Keep v3 path in the reader so existing databases
   still work during the transition period. After v4 becomes the baseline,
   the v3 path can be deleted.

3. **File format version bump in Core**: `FileFormatVersion` constant is
   currently `3`. Either change it to `4` and drop v3, or make the reader
   accept `3` and `4` simultaneously. For the POC: accept both.

4. **Subsetting interaction**: The subsetting (sampling) divides the quad range
   `[startIndex, nextStartIndex)`. In v4 the float section is offset by
   `totalQuads * 6`, so the subset start in the float section is
   `totalQuads * 6 + startIndex * 12`. Double-check that cached
   `QuadsForSubset` entries are still valid after the path change.

---

## Files to create / modify

| Action | File |
|--------|------|
| CREATE | `src/WatneyAstrometry.QuadDbConverter/QuadDbConverter.csproj` |
| CREATE | `src/WatneyAstrometry.QuadDbConverter/Program.cs` |
| MODIFY | `src/WatneyAstrometry.Core/QuadDb/QuadDatabaseCellFile.cs` — version field, v4 dispatch, `ProcessSubCellMergeJoin` |

The index reader (`QuadDatabaseCellFileDescriptor.cs`) and the index writer
(`QuadDatabaseIndexFile.cs`) require **no changes**.

---

## Expected perf impact

| Stage | Before | After |
|-------|--------|-------|
| R0 comparisons per DB quad | O(log 330) ≈ 9 | 1 (imgJ advance amortised) |
| R1–R4 decode per DB quad | always | only when R0 window has a candidate (~2.2%) |
| Float decode per DB quad | always (BytesToQuadNew always decodes) | only on full match (~0.0005%) |
| Total ops per subcell (1000 quads, 330 img) | ~9000 | ~1330 + ~22 R1-R4 decodes |
