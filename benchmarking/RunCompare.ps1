$solverBase = "C:\Dev\WatneyAstrometry\src\WatneyAstrometry.SolverApp\bin\Release"
$threadCounts = @(1, 4, 12)
$modes = @("blind", "nearby")
$variants = @(
    @{ Name = "net6_qdb3";  Exe = "net6.0\watney-solve.exe";  Config = "net10.0\watney-solve-config3.yml" },
    @{ Name = "net10_qdb3"; Exe = "net10.0\watney-solve.exe"; Config = "net10.0\watney-solve-config3.yml" },
    @{ Name = "net10_qdb4"; Exe = "net10.0\watney-solve.exe"; Config = "net10.0\watney-solve-config4.yml" }
)

foreach ($threads in $threadCounts) {
    foreach ($mode in $modes) {
        foreach ($v in $variants) {
            $outFile = "${mode}_$($v.Name)_t${threads}.csv"
            Write-Host "=== $outFile ==="
            .\RunPerfTest.ps1 -Config ".\${mode}-config.json" `
                -SolverExe "$solverBase\$($v.Exe)" `
                -SolverConfig "$solverBase\$($v.Config)" `
                -OutFile $outFile `
                -LimitThreads $threads
        }
    }
}
