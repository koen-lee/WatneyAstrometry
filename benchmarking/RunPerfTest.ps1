# A script that reads a JSON config, runs the solver accordingly
# and saves the benchmarking output as CSV.
# Each image/sampling combination is run $Runs times; the minimum of each
# timing value is recorded to reduce OS scheduling / cache noise.
param(
    [Parameter(Mandatory=$true)]
    [string]
    $Config,

    [Parameter(Mandatory=$true)]
    [string]
    $SolverExe,

    [Parameter(Mandatory=$true)]
    [string]
    $SolverConfig,

    [Parameter(Mandatory=$true)]
    [string]
    $OutFile,

    [Parameter(Mandatory=$false)]
    [int]
    $LimitThreads = 4,

    [Parameter(Mandatory=$false)]
    [int]
    $Runs = 3
)

$ErrorActionPreference = "Stop"

$defaultArgs = @(
    "--benchmark",
    "--extended",
    "--max-stars", "300",
    "--limit-threads", "$LimitThreads",
    "--use-config", $SolverConfig
);

$cJson = Get-Content -Raw -Encoding ascii $Config;
$c = ConvertFrom-Json -InputObject $cJson;

$tableFields = @("Image", "Width px", "Height px", "Success",
    "Stars detected", "Stars used", "Sampling", "Image read (s)",
    "Star detection (s)", "Quad formation (s)", "Solver (s)", "Full process (s)",
    "Field radius");

$headerRow = [string]::Join(";", $tableFields);
Set-Content -Encoding ascii -Path $OutFile $headerRow;

Write-Host "Starting... ($Runs run(s) per image/sampling, keeping minimum timings)"

# Warmup run: solve the first image/sampling once to warm OS file caches,
# then discard the result before the measured runs begin.
$warmupSampling = $c.sampling[0];
$warmupImage = $c.images[0];
Write-Host "Warmup run (sampling=$warmupSampling, image=$warmupImage)..."
$warmupArgs = @(
    $c.mode,
    "--sampling", $warmupSampling,
    "-i", $warmupImage
);
if($c.mode -eq "blind") {
    $warmupArgs += @("--min-radius", "0.5", "--max-radius", "8");
}
else {
    $warmupArgs += @("--search-radius", "10", "-m");
    $warmupArgs += @("--ra", $c.imageParams[0].ra);
    $warmupArgs += @("--dec", $c.imageParams[0].dec);
    $warmupArgs += @("--field-radius-range", $c.imageParams[0].field);
    $warmupArgs += @("--field-radius-steps", $c.imageParams[0].steps);
}
$warmupArgs += $defaultArgs;
& $SolverExe @warmupArgs | Out-Null;
Write-Host "Warmup done."

# Timing field names as they appear in solver output (matched by regex)
$timingPatterns = [ordered]@{
    "imageRead"     = "IMAGEREAD_DURATION: (\d+\.*\d*)"
    "starDetection" = "STARDETECTION_DURATION: (\d+\.*\d*)"
    "quadFormation" = "QUADFORMATION_DURATION: (\d+\.*\d*)"
    "solve"         = "SOLVE_DURATION: (\d+\.*\d*)"
    "full"          = "FULL_DURATION: (\d+\.*\d*)"
}

for($s = 0; $s -lt $c.sampling.Length; $s++) {
    $sampling = $c.sampling[$s];
    Write-Host "Sampling $sampling";

    for($i = 0; $i -lt $c.images.Length; $i++) {
        $image = $c.images[$i];
        Write-Host "  Image $image";

        $solverArgs = @(
            $c.mode,
            "--sampling", $c.sampling[$s],
            "-i", $image
        );
        if($c.mode -eq "blind") {
            $solverArgs += @(
                "--min-radius", "0.5",
                "--max-radius", "8"
            );
        }
        else {
            $solverArgs += @(
                "--search-radius", "10",
                "-m"
            );
            $imageNearbyParams = $c.imageParams[$i];
            $solverArgs += @("--ra", $imageNearbyParams.ra);
            $solverArgs += @("--dec", $imageNearbyParams.dec);
            $solverArgs += @("--field-radius-range", $imageNearbyParams.field);
            $solverArgs += @("--field-radius-steps", $imageNearbyParams.steps);
        }
        $solverArgs += $defaultArgs;

        # --- run $Runs times, collect timings ---
        $bestTimings = @{}
        foreach($key in $timingPatterns.Keys) { $bestTimings[$key] = [double]::MaxValue }
        $firstOutput = $null

        for($r = 0; $r -lt $Runs; $r++) {
            Write-Host "  Run $($r+1)/$Runs...";
            $output = & $SolverExe @solverArgs;
            if($r -eq 0) { $firstOutput = $output }

            foreach($key in $timingPatterns.Keys) {
                $val = [double]([regex]::Match($output, $timingPatterns[$key]).Groups[1].Value)
                if($val -lt $bestTimings[$key]) { $bestTimings[$key] = $val }
            }
        }

        # --- build CSV row: static fields from first run, timings = minimums ---
        # Use InvariantCulture when formatting doubles so the CSV always uses '.'
        # as decimal separator regardless of the Windows locale setting.
        $ic = [System.Globalization.CultureInfo]::InvariantCulture
        $row = @($c.images[$i]);
        $row += [regex]::Match($firstOutput, '"imageWidth": (\d+)').Groups[1].Value
        $row += [regex]::Match($firstOutput, '"imageHeight": (\d+)').Groups[1].Value
        $row += [regex]::Match($firstOutput, '"success": (true|false)').Groups[1].Value
        $row += [regex]::Match($firstOutput, '"starsDetected": (\d+)').Groups[1].Value
        $row += [regex]::Match($firstOutput, '"starsUsed": (\d+)').Groups[1].Value
        $row += $c.sampling[$s];
        $row += $bestTimings["imageRead"].ToString($ic)
        $row += $bestTimings["starDetection"].ToString($ic)
        $row += $bestTimings["quadFormation"].ToString($ic)
        $row += $bestTimings["solve"].ToString($ic)
        $row += $bestTimings["full"].ToString($ic)
        $row += [regex]::Match($firstOutput, '"fieldRadius": (\d+\.*\d*)').Groups[1].Value

        $rowString = [string]::Join(";", $row);
        Add-Content -Encoding ascii -Path $OutFile $rowString;
    }
}
