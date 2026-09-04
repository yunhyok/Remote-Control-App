param()

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $projectRoot 'src\RemoteMonitorMaster\RemoteMonitorMaster.csproj'
$output = Join-Path $projectRoot 'src\RemoteMonitorMaster\bin\Release\net48'
$dist = Join-Path $projectRoot 'dist'
$zip = Join-Path $dist 'Remote-Monitor-Master-v0.1.0-win7-net48.zip'

dotnet restore $project
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }
dotnet build $project -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

$selfTest = Start-Process -FilePath (Join-Path $output 'RemoteMonitorMaster.exe') -ArgumentList '--self-test' -Wait -PassThru
if ($selfTest.ExitCode -ne 0) { throw "Self-test failed with exit code $($selfTest.ExitCode)." }
Write-Host 'Self-test passed.'

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
    (Join-Path $output 'RemoteMonitorMaster.exe'),
    (Join-Path $output 'RemoteMonitorMaster.exe.config'),
    (Join-Path $projectRoot 'README.md'),
    (Join-Path $projectRoot 'WIN7-TEST.md')
)
Compress-Archive -LiteralPath $packageFiles -DestinationPath $zip -CompressionLevel Optimal

$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Value "$hash  $([IO.Path]::GetFileName($zip))" -Encoding ASCII
Write-Host "Package: $zip"
Write-Host "SHA256: $hash"
