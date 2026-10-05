$ErrorActionPreference = 'Stop'

try {
    $output = Join-Path $PSScriptRoot 'src\MeetingCapture\bin\Release\net10.0-windows10.0.19041.0\win-arm64'
    $exe = Join-Path $output 'MeetingCapture.exe'
    $running = Get-Process -Name MeetingCapture -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($running) {
        if (-not $running.Path) { throw "Cannot locate the running MeetingCapture executable (PID $($running.Id))." }
        Start-Process -FilePath $running.Path -WorkingDirectory (Split-Path $running.Path)
        return
    }

    $project = Join-Path $PSScriptRoot 'src\MeetingCapture\MeetingCapture.csproj'
    $buildArgs = @('build', $project, '-c', 'Release', '-r', 'win-arm64')
    $cli = Join-Path $output 'runtimes\win-arm64\native\copilot.exe'
    if (Test-Path $cli -PathType Leaf) {
        $buildArgs += "-p:CopilotCliBinaryPath=$cli"
    }
    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "MeetingCapture build failed (exit code $LASTEXITCODE)." }

    foreach ($file in @('MeetingCapture.exe', 'MeetingCapture.pri', 'MeetingCapture.runtimeconfig.json', 'MeetingCapture.deps.json')) {
        if (-not (Test-Path (Join-Path $output $file) -PathType Leaf)) {
            throw "MeetingCapture build is incomplete: missing $file."
        }
    }
    Start-Process -FilePath $exe -WorkingDirectory $output
}
catch {
    Write-Host "Meeting Capture could not start: $($_.Exception.Message)" -ForegroundColor Red
    Read-Host 'Press Enter to close' | Out-Null
    exit 1
}
