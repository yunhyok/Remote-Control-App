param([switch]$SlaveOnly)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $projectRoot 'src\RemoteMonitorMaster\RemoteMonitorMaster.csproj'
$output = Join-Path $projectRoot 'src\RemoteMonitorMaster\bin\Release\net48'
$slaveProject = Join-Path $projectRoot 'src\RemoteMonitorSlave\RemoteMonitorSlave.csproj'
$slaveOutput = Join-Path $projectRoot 'src\RemoteMonitorSlave\bin\Release\net48'
$dist = Join-Path $projectRoot 'dist'
$zip = Join-Path $dist $(if ($SlaveOnly) { 'Remote-Monitor-Slave-v0.1.48-win11-net48.zip' } else { 'Remote-Monitor-v0.1.48-win7-win11-net48.zip' })
$targetProjects = if ($SlaveOnly) { @($slaveProject) } else { @($project, $slaveProject) }
$targetExecutables = if ($SlaveOnly) { @((Join-Path $slaveOutput 'RemoteMonitorSlave.exe')) } else {
    @((Join-Path $output 'RemoteMonitorMaster.exe'), (Join-Path $slaveOutput 'RemoteMonitorSlave.exe'))
}

foreach ($targetProject in $targetProjects) {
    dotnet restore $targetProject
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }
    dotnet build $targetProject -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
}
foreach ($executable in $targetExecutables) {
    $verification = Join-Path $dist 'verification-v0.1.48'
    New-Item -ItemType Directory -Path $verification -Force | Out-Null
    $testName = [IO.Path]::GetFileNameWithoutExtension($executable)
    $selfTest = Start-Process -FilePath $executable -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru `
        -RedirectStandardOutput (Join-Path $verification ($testName + '.stdout.log')) `
        -RedirectStandardError (Join-Path $verification ($testName + '.stderr.log'))
    if ($selfTest.ExitCode -ne 0) { throw "Self-test failed with exit code $($selfTest.ExitCode): $executable" }
    Write-Host "Self-test passed: $([IO.Path]::GetFileName($executable))"
}

$rootPrefix = $projectRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
foreach ($target in @($zip, (Join-Path $dist 'SHA256SUMS.txt'))) {
    $fullTarget = [IO.Path]::GetFullPath($target)
    if (-not $fullTarget.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to replace path outside project: $fullTarget"
    }
}

if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$packageFiles = @(
    (Join-Path $slaveOutput 'RemoteMonitorSlave.exe'),
    (Join-Path $slaveOutput 'RemoteMonitorSlave.exe.config'),
    (Join-Path $projectRoot 'README.md'),
    (Join-Path $projectRoot 'HANDOFF.md'),
    (Join-Path $projectRoot 'SLAVE-TEST.md')
)
if (-not $SlaveOnly) {
    $packageFiles += (Join-Path $output 'RemoteMonitorMaster.exe'), (Join-Path $output 'RemoteMonitorMaster.exe.config'), (Join-Path $projectRoot 'WIN7-TEST.md')
}
Compress-Archive -LiteralPath $packageFiles -DestinationPath $zip -CompressionLevel Optimal

$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Value "$hash  $([IO.Path]::GetFileName($zip))" -Encoding ASCII
Write-Host "Package: $zip"
Write-Host "SHA256: $hash"
