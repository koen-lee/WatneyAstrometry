// Copyright (c) Jussi Saarivirta.
// Licensed under the Apache License, Version 2.0.

namespace WatneyAstrometry.Core.StarDetection
{
    /// <summary>
    /// Reads a single pixel value from a raw byte buffer, normalising it to a non-negative ushort.
    /// The normalisation / bit depth reduction ensures that all further processing can be done using 16-bit values, 
    /// which is a good balance between precision and memory usage.
    /// The use case is detecting stars against a black background, so we don't need the full precision of 32-bit floats or even 16-bit integers.
    /// One struct implementation exists per supported bit-depth.
    /// The generic-struct + interface pattern lets the JIT specialise (and fully inline)
    /// every call site — no virtual dispatch, no delegate overhead.
    /// </summary>
    internal interface IPixelReader
    {
        unsafe ushort Read(byte* ptr, int bytePos);
    }

    internal readonly struct PixelReader8 : IPixelReader
    {
        public unsafe ushort Read(byte* ptr, int bytePos) => ptr[bytePos];
    }

    internal readonly struct PixelReader16 : IPixelReader
    {
        public unsafe ushort Read(byte* ptr, int bytePos)
        {
            short val = (short)(ptr[bytePos] << 8 | ptr[bytePos + 1]);
            return (ushort)(val - short.MinValue);
        }
    }

    internal readonly struct PixelReader32 : IPixelReader
    {
        // downsample 32-bit to 16 bit by taking the most significant 16 bits, and normalising to unsigned range.
        public unsafe ushort Read(byte* ptr, int bytePos)
        {
            int val = (short)(ptr[bytePos] << 8 | ptr[bytePos + 1] );
            return (ushort)(val - short.MinValue);
        }
    }
}
