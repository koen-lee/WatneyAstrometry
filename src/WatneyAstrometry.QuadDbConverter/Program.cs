// Quad Database v3 → v4 Converter
// Converts AoS layout to SoA layout, sorting quads within each subcell by R0 ascending.
// Usage: QuadDbConverter --input <v3-db-dir> --output <v4-db-dir> [--parallel <n>]

using System.Text;

const string QdbIdentifier = "WATNEYQDB";
const string IndexIdentifier = "WATNEYQDBINDEX";
const int QdbHeaderSize = 9 + 4; // identifier + version int32
const int QuadDataLen = 18;       // 6 ratio bytes + 12 float bytes

// --- Parse args ---
string? inputDir = null, outputDir = null;
int parallelism = Environment.ProcessorCount;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--input"    && i + 1 < args.Length) { inputDir  = args[++i]; }
    else if (args[i] == "--output"   && i + 1 < args.Length) { outputDir = args[++i]; }
    else if (args[i] == "--parallel" && i + 1 < args.Length) { parallelism = int.Parse(args[++i]); }
}

if (inputDir == null || outputDir == null)
{
    Console.Error.WriteLine("Usage: QuadDbConverter --input <v3-db-dir> --output <v4-db-dir> [--parallel <n>]");
    return 1;
}

if (!Directory.Exists(inputDir))
{
    Console.Error.WriteLine($"Input directory not found: {inputDir}");
    return 1;
}

Directory.CreateDirectory(outputDir);

// --- Collect all file jobs from index files ---
var indexFiles = Directory.GetFiles(inputDir, "*.qdbindex");
if (indexFiles.Length == 0)
{
    Console.Error.WriteLine("No .qdbindex files found in input directory.");
    return 1;
}

// A job = one .qdb data file to convert, with its subcell layout.
var jobs = new List<ConversionJob>();

foreach (var indexPath in indexFiles)
{
    var outIndexPath = Path.Combine(outputDir, Path.GetFileName(indexPath));
    File.Copy(indexPath, outIndexPath, overwrite: true);
    Console.WriteLine($"Copied index: {Path.GetFileName(indexPath)}");

    var descriptors = ReadIndexFile(indexPath);
    foreach (var desc in descriptors)
    {
        var inQdb  = Path.Combine(inputDir,  desc.Filename);
        var outQdb = Path.Combine(outputDir, desc.Filename);
        jobs.Add(new ConversionJob(inQdb, outQdb, desc.Passes));
    }
}

Console.WriteLine($"Converting {jobs.Count} .qdb files using {parallelism} thread(s)...");

int converted = 0;
var pOptions = new ParallelOptions { MaxDegreeOfParallelism = parallelism };
Parallel.ForEach(jobs, pOptions, job =>
{
    try
    {
        ConvertFile(job);
        int n = Interlocked.Increment(ref converted);
        if (n % 50 == 0 || n == jobs.Count)
            Console.WriteLine($"  {n}/{jobs.Count}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ERROR converting {job.InputPath}: {ex.Message}");
    }
});

Console.WriteLine("Done.");
return 0;

// ─────────────────────────────────────────────────────────────────────────────
// Index parsing
// ─────────────────────────────────────────────────────────────────────────────

static List<FileDescriptor> ReadIndexFile(string path)
{
    using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    using var rdr = new BinaryReader(stream);

    // Header: identifier (14 bytes) + version (1 byte) + isLittleEndian (1 byte)
    var idBytes = rdr.ReadBytes(IndexIdentifier.Length);
    if (Encoding.ASCII.GetString(idBytes) != IndexIdentifier)
        throw new InvalidDataException($"{path}: not a valid qdbindex file");

    byte indexVersion = rdr.ReadByte();
    if (indexVersion != 1)
        throw new InvalidDataException($"{path}: unsupported index version {indexVersion}");

    bool isLittleEndian = rdr.ReadByte() == 1;
    bool swap = isLittleEndian != BitConverter.IsLittleEndian;

    var descriptors = new List<FileDescriptor>();
    while (stream.Position < stream.Length)
        descriptors.Add(ReadDescriptor(rdr, swap));

    return descriptors;
}

static FileDescriptor ReadDescriptor(BinaryReader rdr, bool swap)
{
    byte filenameLen = rdr.ReadByte();
    string filename = Encoding.UTF8.GetString(rdr.ReadBytes(filenameLen));

    // band, cell, passCount  (3 × int32)
    int _band      = ReadInt32(rdr, swap);
    int _cell      = ReadInt32(rdr, swap);
    int passCount  = ReadInt32(rdr, swap);

    var passes = new PassDescriptor[passCount];
    for (int p = 0; p < passCount; p++)
    {
        float _quadsPerSqDeg = ReadFloat(rdr, swap);
        int   _subDivisions  = ReadInt32(rdr, swap);
        int   numSubCells    = ReadInt32(rdr, swap);

        var subCells = new SubCellDescriptor[numSubCells];
        for (int sc = 0; sc < numSubCells; sc++)
        {
            float _ra  = ReadFloat(rdr, swap);
            float _dec = ReadFloat(rdr, swap);
            int dataLen = ReadInt32(rdr, swap);
            subCells[sc] = new SubCellDescriptor(dataLen);
        }
        passes[p] = new PassDescriptor(subCells);
    }

    // Compute sequential DataStartPos (v3/v4 header is always 13 bytes)
    long pos = QdbHeaderSize;
    foreach (var pass in passes)
        foreach (var sc in pass.SubCells)
        {
            sc.DataStartPos = pos;
            pos += sc.DataLengthBytes;
        }

    return new FileDescriptor(filename, passes);
}

static int ReadInt32(BinaryReader rdr, bool swap)
{
    var b = rdr.ReadBytes(4);
    if (swap) Array.Reverse(b);
    return BitConverter.ToInt32(b, 0);
}

static float ReadFloat(BinaryReader rdr, bool swap)
{
    var b = rdr.ReadBytes(4);
    if (swap) Array.Reverse(b);
    return BitConverter.ToSingle(b, 0);
}

// ─────────────────────────────────────────────────────────────────────────────
// Conversion
// ─────────────────────────────────────────────────────────────────────────────

static void ConvertFile(ConversionJob job)
{
    Directory.CreateDirectory(Path.GetDirectoryName(job.OutputPath)!);

    using var inStream  = new FileStream(job.InputPath,  FileMode.Open,   FileAccess.Read,  FileShare.Read);
    using var outStream = new FileStream(job.OutputPath, FileMode.Create, FileAccess.Write, FileShare.None);

    // Validate input header
    var idBuf = new byte[QdbIdentifier.Length];
    inStream.Read(idBuf, 0, idBuf.Length);
    if (Encoding.ASCII.GetString(idBuf) != QdbIdentifier)
        throw new InvalidDataException($"Not a valid .qdb file: {job.InputPath}");

    var verBuf = new byte[4];
    inStream.Read(verBuf, 0, 4);
    int inputVersion = BitConverter.ToInt32(verBuf, 0);
    if (inputVersion != 3)
        throw new InvalidDataException($"Expected v3 database, got version {inputVersion}: {job.InputPath}");

    // Write v4 header
    outStream.Write(Encoding.ASCII.GetBytes(QdbIdentifier));
    outStream.Write(BitConverter.GetBytes(4)); // version = 4

    // Convert each subcell
    var quadBuf = Array.Empty<byte>();

    foreach (var pass in job.Passes)
    {
        foreach (var sc in pass.SubCells)
        {
            int quadCount = sc.DataLengthBytes / QuadDataLen;

            if (quadBuf.Length < sc.DataLengthBytes)
                quadBuf = new byte[sc.DataLengthBytes];

            inStream.Seek(sc.DataStartPos, SeekOrigin.Begin);
            inStream.ReadExactly(quadBuf, 0, sc.DataLengthBytes);

            // Sort by R0 (10-bit integer at bits [0..9] of the 6-byte packed ratio field).
            // Extract keys to avoid unsafe-pointer-in-lambda issue.
            int[] r0Keys = new int[quadCount];
            for (int i = 0; i < quadCount; i++)
                r0Keys[i] = R0Raw(quadBuf, i);

            int[] order = new int[quadCount];
            for (int i = 0; i < quadCount; i++) order[i] = i;

            Array.Sort(r0Keys, order); // order[i] = original index of i-th R0-sorted quad

            // Write SoA: ratio section first, float section second (bytes copied verbatim).
            for (int i = 0; i < quadCount; i++)
                outStream.Write(quadBuf, order[i] * QuadDataLen, 6);       // ratios

            for (int i = 0; i < quadCount; i++)
                outStream.Write(quadBuf, order[i] * QuadDataLen + 6, 12);  // floats
        }
    }
}

// Extract 10-bit R0 raw integer from quad at index qi in quadBuf.
static int R0Raw(byte[] buf, int qi)
{
    int b = qi * QuadDataLen;
    return ((buf[b + 1] & 0x03) << 8) | buf[b];
}

// ─────────────────────────────────────────────────────────────────────────────
// Data types
// ─────────────────────────────────────────────────────────────────────────────

record FileDescriptor(string Filename, PassDescriptor[] Passes);
record PassDescriptor(SubCellDescriptor[] SubCells);

class SubCellDescriptor(int dataLengthBytes)
{
    public int  DataLengthBytes { get; } = dataLengthBytes;
    public long DataStartPos    { get; set; }
}

record ConversionJob(string InputPath, string OutputPath, PassDescriptor[] Passes);
