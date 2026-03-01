// Copyright (c) Jussi Saarivirta.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using WatneyAstrometry.Core.Exceptions;
using WatneyAstrometry.Core.Types;

namespace WatneyAstrometry.Core.QuadDb
{
    /// <summary>
    /// A class that represents a single Cell file (a file that contains quads in
    /// passes for specified RA,Dec bounds, a part of the quad database).
    /// </summary>
    internal class QuadDatabaseCellFile : IDisposable
    {
        public QuadDatabaseCellFileDescriptor Descriptor { get; private set; }
        private const int QuadDataLen = /*ratios*/ 6 + /*largestDist*/ sizeof(float) + /*coords*/ sizeof(float) * 2;

        private readonly bool _bytesNeedReversing = false;

        public int FileId => _fileId;
        private readonly int _fileId;

        private int _fileFormatVersion = 0;
        private const string FileFormatIdentifierString = "WATNEYQDB";
        private const int FileFormatVersionV3 = 3;
        private const int FileFormatVersionV4 = 4;

        // Memory-mapped file state. Initialized lazily on first access.
        private volatile bool _mapped = false;
        private readonly object _mapLock = new object();
        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _accessor;
        private unsafe byte* _pBase;

        public QuadDatabaseCellFile(QuadDatabaseCellFileDescriptor descriptor, int fileId)
        {
            Descriptor = descriptor;
            _fileId = fileId;
            _bytesNeedReversing = descriptor.BytesNeedReversing;
        }

        private unsafe void EnsureMapped()
        {
            if (_mapped) return;
            lock (_mapLock)
            {
                if (_mapped) return;
                try
                {
                    _mmf = MemoryMappedFile.CreateFromFile(
                        Descriptor.Filename, FileMode.Open, null, 0,
                        MemoryMappedFileAccess.Read);
                    _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                    byte* ptr = null;
                    _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                    _pBase = ptr;
                }
                catch (FileNotFoundException)
                {
                    throw new QuadDatabaseException(
                        $"Quad database file {Descriptor.Filename} was not found. Is your quad database intact?");
                }
                catch (Exception e)
                {
                    throw new QuadDatabaseException(
                        $"Failed to read quad database file {Descriptor.Filename}: {e.Message}", e);
                }

                // Validate file identifier.
                var idBytes = new ReadOnlySpan<byte>(_pBase, FileFormatIdentifierString.Length);
                if (Encoding.ASCII.GetString(idBytes) != FileFormatIdentifierString)
                    throw new QuadDatabaseVersionException(
                        $"The file {Descriptor.Filename} is not a valid Watney database file");

                // Read version number. Offset 9 is not 4-byte aligned, so copy bytes explicitly
                // rather than dereferencing as int* (same ARM alignment concern as for quad floats).
                byte* pVer = _pBase + FileFormatIdentifierString.Length;
                var versionBytes = new byte[] { pVer[0], pVer[1], pVer[2], pVer[3] };
                if (_bytesNeedReversing) Array.Reverse(versionBytes);
                int version = BitConverter.ToInt32(versionBytes, 0);
                if (version != FileFormatVersionV3 && version != FileFormatVersionV4)
                    throw new QuadDatabaseVersionException(
                        $"Expected database version {FileFormatVersionV3} or {FileFormatVersionV4} format database files, but they were version {version}. " +
                        $"Unable to use them. Make sure you have downloaded the right database files.");
                _fileFormatVersion = version;

                _mapped = true;
            }
        }

        /// <summary>
        /// Get the quads within range that are potential matches for the image quads.
        /// </summary>
        /// <param name="center"></param>
        /// <param name="angularDistance"></param>
        /// <param name="passIndex"></param>
        /// <param name="numSubSets">The number of quad subsets we divide the available quads to.</param>
        /// <param name="subSetIndex">The index of the quad subset we want to include.</param>
        /// <param name="sortedImageQuads">Image star quads, sorted by the first ratio (descending).</param>
        /// <param name="cache"></param>
        /// <returns></returns>
        public unsafe StarQuad[] GetQuads(EquatorialCoords center, double angularDistance, int passIndex,
            int numSubSets, int subSetIndex, ImageStarQuad[] sortedImageQuads, QuadDatabaseSolveInstanceMemoryCache cache)
        {
            // Quads that get a match, and are within search distance
            var matchingQuadsWithinRange = new List<StarQuad>();

            // Quads that get a match
            var matchingQuads = new List<StarQuad>();


            var pass = Descriptor.Passes[passIndex];
            var subCellsInRangeArr = new QuadDatabaseCellFileDescriptor.SubCellInfo[pass.SubCells.Length];
            var subCellsInRangeIndexesArr = new int[pass.SubCells.Length];
            var subCellsInRangeLen = 0;

            var decThreshold = angularDistance + pass.AvgSubCellRadius;
            int d = pass.SubDivisions;
            var subCells = pass.SubCells;

            for (int r = 0; r < d; r++)
            {
                int rowBase = r * d;

                // Dec pre-filter: all cells in this row share the same Dec center.
                if (Math.Abs(subCells[rowBase].Center.Dec - center.Dec) >= decThreshold)
                    continue;

                var scLeft  = subCells[rowBase];
                var scRight = subCells[rowBase + d - 1];
                bool leftIn  = scLeft.Center.GetAngularDistanceTo(center)  < decThreshold;
                bool rightIn = scRight.Center.GetAngularDistanceTo(center) < decThreshold;

                if (leftIn && rightIn)
                {
                    // Angular distance to the search center is unimodal in RA along a
                    // fixed-Dec row, so the endpoints are the maximum-distance points.
                    // Both endpoints in range means every cell in the row is in range.
                    for (int c = 0; c < d; c++)
                    {
                        subCellsInRangeArr[subCellsInRangeLen]        = subCells[rowBase + c];
                        subCellsInRangeIndexesArr[subCellsInRangeLen] = rowBase + c;
                        subCellsInRangeLen++;
                    }
                    continue;
                }

                // Fan out from the nearest column (minimum angular distance in this row).
                double raStep  = (scRight.Center.Ra - scLeft.Center.Ra) / (d - 1);
                int nearCol = Math.Clamp((int)Math.Round((center.Ra - scLeft.Center.Ra) / raStep), 0, d - 1);

                // If the minimum-distance column fails, all others in this row fail too.
                if (subCells[rowBase + nearCol].Center.GetAngularDistanceTo(center) >= decThreshold)
                    continue;
                subCellsInRangeArr[subCellsInRangeLen]        = subCells[rowBase + nearCol];
                subCellsInRangeIndexesArr[subCellsInRangeLen] = rowBase + nearCol;
                subCellsInRangeLen++;

                // Walk left from nearCol, stopping at first failure.
                for (int c = nearCol - 1; c >= 0; c--)
                {
                    var sc = subCells[rowBase + c];
                    if (sc.Center.GetAngularDistanceTo(center) >= decThreshold) break;
                    subCellsInRangeArr[subCellsInRangeLen]        = sc;
                    subCellsInRangeIndexesArr[subCellsInRangeLen] = rowBase + c;
                    subCellsInRangeLen++;
                }

                // Walk right from nearCol, stopping at first failure.
                for (int c = nearCol + 1; c < d; c++)
                {
                    var sc = subCells[rowBase + c];
                    if (sc.Center.GetAngularDistanceTo(center) >= decThreshold) break;
                    subCellsInRangeArr[subCellsInRangeLen]        = sc;
                    subCellsInRangeIndexesArr[subCellsInRangeLen] = rowBase + c;
                    subCellsInRangeLen++;
                }
            }

            var thisFileCache = cache.Files[_fileId];

            if (subCellsInRangeLen == 0)
                return Array.Empty<StarQuad>();

            EnsureMapped();
            byte* pBase = _pBase;

            for (var sc = 0; sc < subCellsInRangeLen; sc++)
            {
                var subCellIdx = subCellsInRangeIndexesArr[sc];

                // Need to identify non-sampling cases, and maintain a separate cache for them;
                // When sampling is used, we use a number of subsets (== sampling parameter value) and we
                // cache the matching quads per subset. But when the final matching is done, we need to
                // use all possible quads - but not all are cached yet, so we can't just grab them from
                // all subsets and be done with it. Instead, maintain a separate cache for the non-sampled
                // matching runs. It's just easier that way.
                // Means we build and use the separate cache for non-sampled runs but that's fine.
                bool samplingBeingUsed = numSubSets > 1;
                StarQuad[] cachedQuads = null;

                if (!samplingBeingUsed)
                {
                    cachedQuads = thisFileCache.Passes[passIndex].SubCells[subCellIdx].QuadsFullSet;
                }
                else if(thisFileCache.Passes[passIndex].SubCells[subCellIdx].QuadsForSubset != null)
                {
                    cachedQuads = thisFileCache.Passes[passIndex].SubCells[subCellIdx].QuadsForSubset[subSetIndex];
                }

                if (cachedQuads != null)
                {
                    foreach (var quad in cachedQuads)
                    {
                        if (quad != null && quad.MidPoint.GetAngularDistanceTo(center) < angularDistance)
                            matchingQuadsWithinRange.Add(quad);
                    }
                }
                else
                {
                    if (samplingBeingUsed && thisFileCache.Passes[passIndex].SubCells[subCellIdx].QuadsForSubset == null)
                    {
                        // Should cause no concern with multi-threading, since we're not processing multiple subsets in parallel.
                        thisFileCache.Passes[passIndex].SubCells[subCellIdx].QuadsForSubset = new StarQuad[numSubSets][];
                    }

                    var quadCount = subCellsInRangeArr[sc].DataLengthBytes / QuadDataLen;
                    // We will split the quadCount to numSubSets, and pick the quads in our assigned (sampling) subset.
                    var quadCountPerSubSet = quadCount / numSubSets;
                    var startIndex = quadCountPerSubSet * subSetIndex;
                    var nextStartIndex = subSetIndex == numSubSets - 1
                        ? quadCount
                        : startIndex + quadCountPerSubSet;

                    if (_fileFormatVersion == FileFormatVersionV3)
                    {
                        // V3: interleaved 18-byte records. Point directly into the mapped region — no copy.
                        byte* pSubCellDataBytes = pBase + subCellsInRangeArr[sc].DataStartPos;

                        int advance = startIndex * QuadDataLen;
                        for (var q = startIndex; q < nextStartIndex; q++)
                        {
                            var quad = BytesToQuadNew(pSubCellDataBytes, advance, sortedImageQuads, _bytesNeedReversing);

                            if (quad != null)
                                matchingQuads.Add(quad);
                            if (quad != null && quad.MidPoint.GetAngularDistanceTo(center) < angularDistance)
                                matchingQuadsWithinRange.Add(quad);

                            advance += QuadDataLen;
                        }
                    }
                    else
                    {
                        // V4: ratio section (N×6 bytes) followed by float section (N×12 bytes).
                        // Point directly into the mapped region — no copy, no syscall.
                        V4SubCellReads++;
                        int ratioByteCount = quadCount * 6;
                        byte* pRatioBuf = pBase + subCellsInRangeArr[sc].DataStartPos;
                        long floatSectionBase = subCellsInRangeArr[sc].DataStartPos + ratioByteCount;

                        ProcessSubCellMergeJoin(
                            pRatioBuf, startIndex, nextStartIndex - startIndex, quadCount,
                            sortedImageQuads, _bytesNeedReversing,
                            center, angularDistance,
                            pBase, floatSectionBase,
                            matchingQuads, matchingQuadsWithinRange);
                    }


                    if (samplingBeingUsed)
                    {
                        thisFileCache.Passes[passIndex].SubCells[subCellIdx].QuadsForSubset[subSetIndex] =
                            matchingQuads.ToArray();
                    }
                    else
                    {
                        thisFileCache.Passes[passIndex].SubCells[subCellIdx].QuadsFullSet =
                            matchingQuads.ToArray();
                    }


                }
            }

            return matchingQuadsWithinRange.ToArray();


        }

        private static unsafe void ProcessSubCellMergeJoin(
            byte* pBuf,            // ratio section only: totalQuads × 6 bytes, sorted by R0
            int startIndex,
            int count,
            int totalQuads,
            ImageStarQuad[] imageQuads,
            bool bytesNeedReversing,
            EquatorialCoords center,
            double angularDistance,
            byte* pBase,           // base of the memory-mapped file; used to read float bytes on match
            long floatSectionBase, // file offset of the start of the float section
            List<StarQuad> matchingQuads,
            List<StarQuad> matchingQuadsWithinRange)
        {
            // pBuf contains the full ratio section (totalQuads × 6 bytes).
            // Subset [startIndex, startIndex+count) sits at pBuf + startIndex*6.
            byte* pRatios = pBuf + startIndex * 6;

            int imgJ = 0; // lower-bound pointer into imageQuads; only ever advances

            for (int dbIdx = 0; dbIdx < count; dbIdx++)
            {
                byte* pR = pRatios + dbIdx * 6;

                // Decode R0 for the window check only.
                float r0 = (((pR[1] << 8) & 0x3FF) + (pR[0] & 0x3FF)) * OnePer1023;
                float lo0 = r0 * RatioMatchLow;
                float hi0 = r0 * RatioMatchHigh;

                // Advance imgJ past image quads whose R0 is below the window (never rewinds).
                while (imgJ < imageQuads.Length && imageQuads[imgJ].Ratios.R0 < lo0)
                    imgJ++;

                if (imgJ >= imageQuads.Length) break; // all image quads exhausted

                if (imageQuads[imgJ].Ratios.R0 > hi0) continue; // no candidate in window

                // R0 window has at least one candidate — decode the remaining 4 ratios.
                float r1 = ((((pR[2] & 0x0F) << 6) & 0x3FF) + ((pR[1] >> 2) & 0x3FF)) * OnePer1023;
                float r2 = ((((pR[3] & 0x3F) << 4) & 0x3FF) + ((pR[2] >> 4) & 0x3FF)) * OnePer1023;
                float r3 = ((((pR[4] & 0x7F) << 2) & 0x1FF) + ((pR[3] >> 6) & 0x1FF)) * OnePer511;
                float r4 = (((pR[5]            << 1) & 0x1FF) + ((pR[4] >> 7) & 0x1FF)) * OnePer511;

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

                    // Full match — read the 12 float bytes directly from the mapped region.
                    V4FloatFetches++;
                    byte* pF = pBase + floatSectionBase + (long)(startIndex + dbIdx) * 12;

                    float ld, ra, dec;
                    if (bytesNeedReversing)
                    {
                        ld  = BitConverter.ToSingle(new byte[] { pF[3],  pF[2],  pF[1],  pF[0]  }, 0);
                        ra  = BitConverter.ToSingle(new byte[] { pF[7],  pF[6],  pF[5],  pF[4]  }, 0);
                        dec = BitConverter.ToSingle(new byte[] { pF[11], pF[10], pF[9],  pF[8]  }, 0);
                    }
                    else
                    {
                        // ARM misalignment workaround — read as int, reinterpret as float.
                        var if1 = *(int*)pF;
                        var if2 = *(int*)(pF + 4);
                        var if3 = *(int*)(pF + 8);
                        ld  = *(float*)&if1;
                        ra  = *(float*)&if2;
                        dec = *(float*)&if3;
                    }

                    var quad = new StarQuad(dbRatios, ld, new EquatorialCoords(ra, dec));
                    matchingQuads.Add(quad);
                    if (quad.MidPoint.GetAngularDistanceTo(center) < angularDistance)
                        matchingQuadsWithinRange.Add(quad);
                    break; // one DB quad → at most one image-quad match
                }
            }
        }

        internal static long V4SubCellReads;
        internal static long V4FloatFetches;

        private const float OnePer1023 = 0.0009775171065493f; // (1 / 1023)
        private const float OnePer511 = 0.001956947162f; // (1 / 511)
        private const float RatioMatchLow  = 1.0f - 0.011f; // 0.989
        private const float RatioMatchHigh = 1.0f + 0.011f; // 1.011

        /// <summary>
        /// Returns the first index in <paramref name="arr"/> where R0 &gt;= <paramref name="value"/>
        /// (standard lower_bound over a R0-sorted array).
        /// </summary>
        private static int LowerBound(ImageStarQuad[] arr, float value)
        {
            int left = 0, right = arr.Length;
            while (left < right)
            {
                int mid = (left + right) >> 1;
                if (arr[mid].Ratios.R0 < value)
                    left = mid + 1;
                else
                    right = mid;
            }
            return left;
        }

        /// <summary>
        /// Read the bytes and spit out a quad.
        /// </summary>
        /// <param name="pBuf"></param>
        /// <param name="offset"></param>
        /// <param name="sortedTentativeMatches">If given, we only return the constructed quad if the ratios match to one of the sortedTentativeMatches image quads.
        /// The array must be sorted ascending by R0 so that binary search can be used.
        /// Otherwise do no matching and just read the quad from the buffer and return it.</param>
        /// <param name="bytesNeedReversing">Flag that indicates if we need to reverse byte order because of endianness difference between DB file contents and the system</param>
        /// <returns></returns>
        private static unsafe StarQuad BytesToQuadNew(byte* pBuf, int offset, ImageStarQuad[] sortedTentativeMatches, bool bytesNeedReversing)
        {
            // TODO would it be simpler if we moved the byte reversing stuff to another method? How would it affect performance too?
            bool noMatching = sortedTentativeMatches == null;
            byte* pRatios = (byte*)(pBuf + offset);
            byte* pFloats = (byte*)(pBuf + offset + 6); // floats come after the ratios

            // Ratios are packed; 3x 10 bit numbers, 2x 9 bit numbers.
            float r0 = (((pRatios[1] << 8) & 0x3FF) + ((pRatios[0]) & 0x3FF)) * OnePer1023;
            float r1 = ((((pRatios[2] & 0x0F) << 6) & 0x3FF) + ((pRatios[1] >> 2) & 0x3FF)) * OnePer1023;
            float r2 = ((((pRatios[3] & 0x3F) << 4) & 0x3FF) + ((pRatios[2] >> 4) & 0x3FF)) * OnePer1023;
            float r3 = ((((pRatios[4] & 0x7F) << 2) & 0x1FF) + ((pRatios[3] >> 6) & 0x1FF)) * OnePer511;
            float r4 = ((((pRatios[5]) << 1) & 0x1FF) + ((pRatios[4] >> 7) & 0x1FF)) * OnePer511;

            if (noMatching)
            {
                // Ratios are always written in little endian and since we read byte by byte, endianness doesn't matter here.

                // Floats were written in whichever endianness the database was written in.
                float ld, ra, dec;
                if (bytesNeedReversing)
                {
                    ld  = BitConverter.ToSingle(new byte[] { pFloats[3],  pFloats[2],  pFloats[1],  pFloats[0]  }, 0);
                    ra  = BitConverter.ToSingle(new byte[] { pFloats[7],  pFloats[6],  pFloats[5],  pFloats[4]  }, 0);
                    dec = BitConverter.ToSingle(new byte[] { pFloats[11], pFloats[10], pFloats[9],  pFloats[8]  }, 0);
                }
                else
                {
                    // Note: On ARM, misaligned floats and doubles have to be read/written from memory as int/long and
                    // converted to/from float/double via local variable that is guaranteed to be aligned.
                    // https://github.com/dotnet/runtime/issues/18041
                    // So we have to do this, otherwise ARMv7 breaks with "A datatype misalignment was detected in a load or store instruction."
                    var if1 = *(int*)pFloats; pFloats += sizeof(int);
                    var if2 = *(int*)pFloats; pFloats += sizeof(int);
                    var if3 = *(int*)pFloats;
                    ld  = *(float*)&if1;
                    ra  = *(float*)&if2;
                    dec = *(float*)&if3;
                }

                return new StarQuad(new QuadRatios(r0, r1, r2, r3, r4), ld, new EquatorialCoords(ra, dec));
            }

            var ratios = new QuadRatios(r0, r1, r2, r3, r4);
            var lo = ratios * RatioMatchLow;
            var hi = ratios * RatioMatchHigh;

            // Binary search into the R0-sorted array to skip the ~90% of quads outside the R0 window.
            int startIdx = LowerBound(sortedTentativeMatches, lo.R0);
            for (var q = startIdx; q < sortedTentativeMatches.Length; q++)
            {
                var imgQuad = sortedTentativeMatches[q];
                if (imgQuad.Ratios.R0 > hi.R0) break; // past the R0 window; array is sorted
                if (imgQuad.Ratios.R1 < lo.R1 || imgQuad.Ratios.R1 > hi.R1) continue;
                if (imgQuad.Ratios.R2 < lo.R2 || imgQuad.Ratios.R2 > hi.R2) continue;
                if (imgQuad.Ratios.R3 < lo.R3 || imgQuad.Ratios.R3 > hi.R3) continue;
                if (imgQuad.Ratios.R4 < lo.R4 || imgQuad.Ratios.R4 > hi.R4) continue;
                float ld, ra, dec;
                if (bytesNeedReversing)
                {
                    ld  = BitConverter.ToSingle(new byte[] { pFloats[3],  pFloats[2],  pFloats[1],  pFloats[0]  }, 0);
                    ra  = BitConverter.ToSingle(new byte[] { pFloats[7],  pFloats[6],  pFloats[5],  pFloats[4]  }, 0);
                    dec = BitConverter.ToSingle(new byte[] { pFloats[11], pFloats[10], pFloats[9],  pFloats[8]  }, 0);
                }
                else
                {
                    // Note: On ARM, misaligned floats and doubles have to be read/written from memory as int/long and
                    // converted to/from float/double via local variable that is guaranteed to be aligned.
                    // https://github.com/dotnet/runtime/issues/18041
                    // So we have to do this, otherwise ARMv7 breaks with "A datatype misalignment was detected in a load or store instruction."
                    var if1 = *(int*)pFloats; pFloats += sizeof(int);
                    var if2 = *(int*)pFloats; pFloats += sizeof(int);
                    var if3 = *(int*)pFloats;
                    ld  = *(float*)&if1;
                    ra  = *(float*)&if2;
                    dec = *(float*)&if3;
                }
                return new StarQuad(ratios, ld, new EquatorialCoords(ra, dec));
            }

            return null;
        }

        public unsafe void Dispose()
        {
            if (_mapped)
            {
                _accessor?.SafeMemoryMappedViewHandle.ReleasePointer();
                _accessor?.Dispose();
                _mmf?.Dispose();
            }
        }
    }
}
