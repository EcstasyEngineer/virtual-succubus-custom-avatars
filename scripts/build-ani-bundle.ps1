[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Editor,
    [Parameter(Mandatory = $true)][string]$ProjectPath,
    [Parameter(Mandatory = $true)][string]$Source
)
$ErrorActionPreference = 'Stop'

function Invoke-Unity([string[]]$UnityArguments) {
    # Windows paths cannot contain quotes. Reject unusual arguments rather than
    # constructing an ambiguous command line for the GUI executable.
    foreach ($value in $UnityArguments) {
        if ($value.Contains('"') -or $value.Contains("`n") -or $value.Contains("`r")) {
            throw 'Quotes and newlines are not accepted in Unity arguments.'
        }
    }
    $quoted = ($UnityArguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $process = Start-Process -FilePath $Editor -ArgumentList $quoted -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Unity failed with exit code $($process.ExitCode). Inspect the specified log." }
}

$Editor = (Resolve-Path -LiteralPath $Editor).Path
$Source = (Resolve-Path -LiteralPath $Source).Path
$modelId = Split-Path -Leaf $Source
if ($modelId -cnotmatch '\A[a-z0-9][a-z0-9_-]{0,63}\z') { throw 'Source folder must have a simple lowercase model identifier.' }
$artifactName = if ($modelId -eq '05-kimono') { 'ani-kimono' } else { 'ani-' + $modelId }
$ProjectPath = [IO.Path]::GetFullPath($ProjectPath)
if (Test-Path -LiteralPath $ProjectPath) { throw 'ProjectPath must not already exist; a fresh disposable project is required.' }
foreach ($name in @(($modelId + '.fbx'), 'humanoid.json', 'report.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $Source $name) -PathType Leaf)) { throw "Source file missing: $name" }
}
$helper = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\unity-editor\BuildAniBundle.cs')).Path
$parent = Split-Path -Parent $ProjectPath
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$createLog = $ProjectPath + '.create.log'
Invoke-Unity @('-batchmode', '-nographics', '-quit', '-createProject', $ProjectPath, '-buildTarget', 'Win64', '-logFile', $createLog)
if (-not (Test-Path -LiteralPath (Join-Path $ProjectPath 'ProjectSettings\ProjectVersion.txt'))) {
    throw "Unity did not create the project. Inspect $createLog (including licensing errors)."
}
$editorFolder = Join-Path $ProjectPath 'Assets\Editor'
New-Item -ItemType Directory -Path $editorFolder -Force | Out-Null
Copy-Item -LiteralPath $helper -Destination (Join-Path $editorFolder 'BuildAniBundle.cs')
$output = Join-Path $ProjectPath 'AvatarBundleOutput'
$buildLog = Join-Path $ProjectPath 'avatar-build.log'
Invoke-Unity @('-batchmode', '-nographics', '-quit', '-projectPath', $ProjectPath, '-buildTarget', 'Win64', '-executeMethod', 'BuildAniBundle.PrepareAndBuildBatch', '-avatarSource', $Source, '-avatarOutput', $output, '-logFile', $buildLog)
$bundle = Join-Path $output ($artifactName + '.bundle')
$evidence = Join-Path $output 'build-evidence.txt'
$profile = Join-Path $output 'model-profile.json'
if (-not (Test-Path -LiteralPath $bundle -PathType Leaf) -or -not (Test-Path -LiteralPath $evidence -PathType Leaf) -or -not (Test-Path -LiteralPath $profile -PathType Leaf)) {
    throw "Unity exited without required bundle/evidence outputs. Inspect $buildLog"
}
if ($modelId -in @('01-classic', '05-kimono')) {
    $facePath = $bundle + '.face.json'
    if (-not (Test-Path -LiteralPath $facePath -PathType Leaf)) { throw 'Required runtime facial profile was not generated.' }
    $face = Get-Content -LiteralPath $facePath -Raw | ConvertFrom-Json
    if ($face.schemaVersion -ne 1 -or $face.modelId -cne ('ani-' + $modelId) -or
        $face.mapping -cne 'ani-arkit-v1' -or $face.jawPolicy -cne 'prefab-rest' -or
        $face.bundleSha256 -ne (Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash) {
        throw 'Runtime facial profile does not match the built bundle.'
    }
    Write-Output "Runtime facial profile: $facePath"
}
Write-Output "Bundle: $bundle"
Write-Output "Build evidence: $evidence"
Write-Output "Model profile: $profile"
Write-Output "Log: $buildLog"
Write-Output 'Bundle production does not verify game compatibility or visual quality. Nothing was installed.'
