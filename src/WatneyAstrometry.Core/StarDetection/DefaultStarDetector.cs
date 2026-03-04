// Copyright (c) Jussi Saarivirta.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WatneyAstrometry.Core.Image;

namespace WatneyAstrometry.Core.StarDetection
{
    /// <summary>
    /// Default implementation of the star detector.<br/>
    /// Note: Only supports monochrome data streams. If you wish to use a color image, you must
    /// first convert it to monochrome.
    /// <para>
    /// A star detector reads a source image and sweeps it looking for shapes that
    /// could be interpreted as stars. This implementation reads the (monochrome) image scanline by
    /// scanline from top to bottom and gathers pixels that go over the noise threshold
    /// of the image, and then joins them together with adjacent detected pixels to form
    /// "bins" of star pixels, each bin representing a star. A filter (see <see cref="IStarDetectionFilter"/>)
    /// is then applied to the bins to discard anomalies like streaks and large blobs.
    /// </para>
    /// <para>
    /// The detection algorithm tries to be simple and efficient, with "good enough" being
    /// the end goal.
    /// </para>
    /// </summary>
    public class DefaultStarDetector : IStarDetector
    {
        private Stream _imageDataStream;
        private Metadata _imageMetadata;
        private long _streamDataPos;

        private Dictionary<long, long> _histogram;
        private long _histogramPeakValue = 0;
        private long _histogramPeakCount = 0;
        private int _bytesPerPixel;
        private double _starDetectionBgOffset;

        private List<StarPixelBin> _starBins = new List<StarPixelBin>();
        private readonly HashSet<StarPixelBin> _absorbedBins = [];
        internal IReadOnlyList<StarPixelBin> StarBins => _starBins;

        /// <summary>
        /// Filter implementation, which filters out undesirable pixel bins.
        /// </summary>
        public IStarDetectionFilter DetectionFilter { get; set; } = new DefaultStarDetectionFilter();
        
        /// <summary>
        /// New instance of star detector.
        /// </summary>
        /// <param name="starDetectionBgOffset">Offset to use for background detection (factor which is used to add n * stdDev to average background value)</param>
        public DefaultStarDetector(double starDetectionBgOffset = 3.0)
        {
            _starDetectionBgOffset = starDetectionBgOffset;
        }


        private void ValidateInputArgs()
        {
            var supportedBpp = new[] {8, 16, 32};
            if(!supportedBpp.Contains(_imageMetadata.BitsPerPixel))
                throw new NotSupportedException($"Unsupported BPP value '{_imageMetadata.BitsPerPixel}'. Supported BPP values are: {string.Join(", ", supportedBpp)}");
        }

        private void Initialize(IImage image)
        {
            _imageDataStream = image.PixelDataStream ?? throw new ArgumentNullException(nameof(image.PixelDataStream));
            _imageMetadata = image.Metadata ?? throw new ArgumentNullException(nameof(image.Metadata));
            _streamDataPos = image.PixelDataStreamOffset;
            _bytesPerPixel = _imageMetadata.BitsPerPixel / 8;
            ValidateInputArgs();
        }

        /// <summary>
        /// Run star detection on image.
        /// </summary>
        /// <param name="image"></param>
        /// <returns></returns>
        public IList<ImageStar> DetectStars(IImage image)
        {
            Initialize(image);

            _imageDataStream.Seek(_streamDataPos, SeekOrigin.Begin);
            if(_histogram == null || !_histogram.Any())
                CreateHistogram();

            _imageDataStream.Seek(_streamDataPos, SeekOrigin.Begin);
            byte[] buf = new byte[_imageMetadata.ImageWidth * _bytesPerPixel];


            var pixelCount = _imageMetadata.ImageWidth * _imageMetadata.ImageHeight;
            var pixelSum = _histogram.Sum(x => x.Key * x.Value);
            var pixelAvg = pixelSum / pixelCount;

            // Too dark or broken image.
            if (pixelAvg == 0)
                return new List<ImageStar>();

            double diffSquared = _histogram.Sum(x => (x.Key - pixelAvg) * (x.Key - pixelAvg) * x.Value);
            double stdDev = Math.Sqrt(diffSquared / pixelCount);

            //long flatValue = pixelAvg + (long)(stdDev * 3);
            long flatValue = pixelAvg + (long)(stdDev * _starDetectionBgOffset);

            switch (_imageMetadata.BitsPerPixel)
            {
                case 8:  RunScanLoop<PixelReader8>(buf, flatValue);  break;
                case 16: RunScanLoop<PixelReader16>(buf, flatValue); break;
                case 32: RunScanLoop<PixelReader32>(buf, flatValue); break;
            }

            if (_absorbedBins.Count > 0)
                _starBins.RemoveAll(_absorbedBins.Contains);

            for (var i = 0; i < _starBins.Count; i++)
                _starBins[i].RecalcLeftRightTopBottom();

            _starBins = DetectionFilter.ApplyFilter(_starBins, _imageMetadata);

            var detectedStars = _starBins.Select(x =>
            {
                var starProps = x.GetCenterPixelPosAndRelativeBrightness();
                return new ImageStar(starProps.PixelPosX, starProps.PixelPosY, starProps.BrightnessValue, starProps.starSize);
            }).ToList();
            
            return detectedStars;


        }

        private void CreateHistogram()
        {
            _histogram = new Dictionary<long, long>();
            _imageDataStream.Seek(_streamDataPos, SeekOrigin.Begin);
            byte[] buf = new byte[_imageMetadata.ImageWidth * _bytesPerPixel];
            switch (_imageMetadata.BitsPerPixel)
            {
                case 8:  FillHistogram<PixelReader8>(buf);  break;
                case 16: FillHistogram<PixelReader16>(buf); break;
                case 32: FillHistogram<PixelReader32>(buf); break;
            }
        }

        private void FillHistogram<TReader>(byte[] buf) where TReader : struct, IPixelReader
        {
            for (var y = 0; y < _imageMetadata.ImageHeight; y++)
            {
                _imageDataStream.ReadExactly(buf, 0, buf.Length);
                AddScanlineToHistogram<TReader>(buf);
            }
        }

        private unsafe void AddScanlineToHistogram<TReader>(byte[] bytes) where TReader : struct, IPixelReader
        {
            var reader = default(TReader);
            fixed (byte* pBuffer = bytes)
            {
                for (int pos = 0; pos < bytes.Length; pos += _bytesPerPixel)
                {
                    long pixelVal = reader.Read(pBuffer, pos);
                    _histogram.TryGetValue(pixelVal, out long count);
                    _histogram[pixelVal] = ++count;
                    if (count > _histogramPeakCount)
                    {
                        _histogramPeakCount = count;
                        _histogramPeakValue = pixelVal;
                    }
                }
            }
        }


        private void RunScanLoop<TReader>(byte[] buf, long flatValue) where TReader : struct, IPixelReader
        {
            _absorbedBins.Clear();
            HashSet<StarPixelBin> previousLineBins = [];
            for (var y = 0; y < _imageMetadata.ImageHeight; y++)
            {
                _imageDataStream.ReadExactly(buf, 0, buf.Length);
                previousLineBins = BinStarPixelsFromScanline<TReader>(buf, y, flatValue, previousLineBins);
            }
        }

        // Algorithm: read whole line into star pixel bins (contiguous pixels over background value on X axis).
        // Then look up one row (x-1 and x+1) for previous line bins. Combine the current bin to that/them
        // (check from left to right, combine self with topleftmost, and potentially the topright with topleftmost too)
        private unsafe HashSet<StarPixelBin> BinStarPixelsFromScanline<TReader>(byte[] bytes, int y, long flatValue, HashSet<StarPixelBin> previousLineBins) where TReader : struct, IPixelReader
        {
            var reader = default(TReader);
            StarPixelBin currentBin = null;
            List<StarPixelBin> scanLineBins = new List<StarPixelBin>();

            // Collect the current pixel line into contiguous star pixel bins.
            fixed (byte* pBuffer = bytes)
            {
                var scanlineByteLen = _imageMetadata.ImageWidth * _bytesPerPixel;
                for (int pos = 0, x = 0; pos < scanlineByteLen; pos += _bytesPerPixel, x++)
                {
                    long val = reader.Read(pBuffer, pos);
                    if (val > flatValue)
                    {
                        if (currentBin == null)
                        {
                            currentBin = new StarPixelBin(x, y, val);
                            scanLineBins.Add(currentBin);
                        }
                        else
                            currentBin.Add(x, y, val);
                    }
                    else
                    {
                        currentBin = null;
                    }
                }
            }

            // No combination to above star pixel bins required if none were present.
            if (previousLineBins.Count == 0)
            {
                _starBins.AddRange(scanLineBins);
                return new HashSet<StarPixelBin>(scanLineBins);
            }

            // Merge into previous line's star pixel bins if they happen to be adjacent.

            // Find the ones above (+-1 px l/r)
            // Take first
            // Add our pixels to that one
            // Add the others' pixels to that one

            HashSet<StarPixelBin> rowOutputBins = new HashSet<StarPixelBin>();

            for (var i = 0; i < scanLineBins.Count; i++)
            {
                var starBin = scanLineBins[i];
                var left = starBin.Left;
                var right = starBin.Right;
                bool merged = false;

                List<StarPixelBin> connectedPreviousLinePixelBins = new List<StarPixelBin>();

                foreach (var prevLineBin in previousLineBins)
                {
                    var prevLineBinPixels = prevLineBin.PixelRows[y - 1];
                    for (var p = 0; p < prevLineBinPixels.Count; p++)
                    {
                        if (prevLineBinPixels[p].X >= left - 1 && prevLineBinPixels[p].X <= right + 1)
                        {
                            connectedPreviousLinePixelBins.Add(prevLineBin);
                            break;
                        }
                    }
                }

                if (connectedPreviousLinePixelBins.Count > 0)
                {
                    merged = true;
                    var mergeTarget = connectedPreviousLinePixelBins[0];
                    var sourceRow = starBin.PixelRows[y];
                    if (!mergeTarget.PixelRows.TryGetValue(y, out var existingy))
                        mergeTarget.PixelRows[y] = new List<StarPixel>(sourceRow);
                    else
                        existingy.AddRange(sourceRow);

                    rowOutputBins.Add(mergeTarget);

                    for (var n = 1; n < connectedPreviousLinePixelBins.Count; n++)
                    {
                        var mergeable = connectedPreviousLinePixelBins[n];
                        foreach (var pixelRow in mergeable.PixelRows)
                        {
                            var k = pixelRow.Key;
                            if (!mergeTarget.PixelRows.TryGetValue(k, out var existingk))
                                mergeTarget.PixelRows[k] = new List<StarPixel>(pixelRow.Value);
                            else
                                existingk.AddRange(pixelRow.Value);
                        }

                        _absorbedBins.Add(mergeable); // Mark absorbed; removed from _starBins after all scanlines.
                        previousLineBins.Remove(mergeable);
                    }
                }

                if (!merged)
                {
                    _starBins.Add(starBin);
                    rowOutputBins.Add(starBin);
                }
            }

            return rowOutputBins;
        }


    }
}
