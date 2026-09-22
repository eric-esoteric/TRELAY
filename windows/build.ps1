param(
    [string]$DotnetPath = 'dotnet',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$windowsRoot = $PSScriptRoot
$repoRoot = Split-Path $windowsRoot -Parent
$project = Join-Path $windowsRoot 'src\TRELAY.csproj'
$output = Join-Path $windowsRoot "src\bin\$Configuration\net48"
$package = Join-Path $windowsRoot 'package\TRELAY-Beta-0.3-Windows-x64'
$artifactDirectory = Join-Path $repoRoot 'artifacts'
$archive = Join-Path $artifactDirectory 'TRELAY-Beta-0.3-Windows-x64.zip'

& $DotnetPath restore $project --configfile (Join-Path $repoRoot 'NuGet.Config') --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed' }

& $DotnetPath build $project -c $Configuration --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed' }

if (Test-Path -LiteralPath $package) {
    $resolvedPackage = (Resolve-Path -LiteralPath $package).Path
    $resolvedWindowsRoot = (Resolve-Path -LiteralPath $windowsRoot).Path
    if (-not $resolvedPackage.StartsWith($resolvedWindowsRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe package path: $resolvedPackage"
    }
    Remove-Item -LiteralPath $resolvedPackage -Recurse -Force
}

New-Item -ItemType Directory -Path $package, $artifactDirectory -Force | Out-Null

foreach ($name in @(
    'TRELAY.exe',
    'TRELAY.exe.config',
    'Microsoft.Bcl.AsyncInterfaces.dll',
    'Renci.SshNet.dll',
    'YamlDotNet.dll'
)) {
    Copy-Item -LiteralPath (Join-Path $output $name) -Destination $package
}

Copy-Item -LiteralPath (Join-Path $windowsRoot 'engine') -Destination $package -Recurse
Copy-Item -LiteralPath (Join-Path $windowsRoot 'providers') -Destination $package -Recurse
Copy-Item -LiteralPath (Join-Path $windowsRoot 'README.md') -Destination $package
Copy-Item -LiteralPath (Join-Path $windowsRoot 'THIRD-PARTY-NOTICES.txt') -Destination $package
Copy-Item -LiteralPath (Join-Path $windowsRoot 'lib\LICENSE-YAMLDOTNET.txt') -Destination $package

if (Test-Path -LiteralPath (Join-Path $package 'engine\app-config.json')) {
    throw 'Runtime engine config leaked into package'
}
if (Test-Path -LiteralPath (Join-Path $package 'engine\logs')) {
    throw 'Runtime engine logs leaked into package'
}

if (Test-Path -LiteralPath $archive) {
    Remove-Item -LiteralPath $archive -Force
}
Compress-Archive -LiteralPath $package -DestinationPath $archive -CompressionLevel Optimal

$item = Get-Item -LiteralPath $archive
[pscustomobject]@{
    Path = $item.FullName
    Length = $item.Length
    SHA256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $item.FullName).Hash
}
