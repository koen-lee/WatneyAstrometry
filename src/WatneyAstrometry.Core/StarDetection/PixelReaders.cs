// Copyright (c) Jussi Saarivirta.
// Licensed under the Apache License, Version 2.0.

namespace WatneyAstrometry.Core.StarDetection
{
    /// <summary>
    /// Reads a single pixel value from a raw byte buffer, normalising it to a non-negative long.
    /// One struct implementation exists per supported bit-depth.
    /// The generic-struct + interface pattern lets the JIT specialise (and fully inline)
    /// every call site — no virtual dispatch, no delegate overhead.
    /// </summary>
    internal interface IPixelReader
    {
        unsafe long Read(byte* ptr, int bytePos);
    }

    internal readonly struct PixelReader8 : IPixelReader
    {
        public unsafe long Read(byte* ptr, int bytePos) => ptr[bytePos];
    }

    internal readonly struct PixelReader16 : IPixelReader
    {
        public unsafe long Read(byte* ptr, int bytePos)
        {
            short val = (short)(ptr[bytePos] << 8 | ptr[bytePos + 1]);
            return (ushort)(val - short.MinValue);
        }
    }

    internal readonly struct PixelReader32 : IPixelReader
    {
        public unsafe long Read(byte* ptr, int bytePos)
        {
            int val = (int)(ptr[bytePos] << 24 | ptr[bytePos + 1] << 16 | ptr[bytePos + 2] << 8 | ptr[bytePos + 3]);
            return (uint)(val - int.MinValue);
        }
    }
}
