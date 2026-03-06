$solverBase = "C:\Dev\WatneyAstrometry\src\WatneyAstrometry.SolverApp\bin\Release"
$threadCounts = @(12)
$modes = @("blind", "nearby")
$variants = @(
    @{ Name = "release_qdb3"; Exe = "net6.0\watney-solve.exe";  Config = "net10.0\watney-solve-config3.yml" },
    @{ Name = "release_qdb4"; Exe = "net6.0\watney-solve.exe";  Config = "net10.0\watney-solve-config4.yml" },
    @{ Name = "publish_qdb3"; Exe = "net10.0\win-x64\publish\watney-solve.exe"; Config = "net10.0\watney-solve-config3.yml" },
    @{ Name = "publish_qdb4"; Exe = "net10.0\win-x64\publish\watney-solve.exe"; Config = "net10.0\watney-solve-config4.yml" }
)
$timestamp = Get-Date -Format "yyyy-MM-dd_HH-mm-ss"
mkdir $timestamp | Out-Null
foreach ($threads in $threadCounts) {
    foreach ($mode in $modes) {
        foreach ($v in $variants) {
            $outFile = "${mode}_$($v.Name)_t${threads}.csv"
            Write-Host "=== $outFile ==="
            .\RunPerfTest.ps1 -Config ".\${mode}-config.json" `
                -SolverExe "$solverBase\$($v.Exe)" `
                -SolverConfig "$solverBase\$($v.Config)" `
                -OutFile "${timestamp}\${outFile}" `
                -LimitThreads $threads
        }
    }
}
Write-Host "Output written to $timestamp"
