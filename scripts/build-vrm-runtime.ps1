[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Editor,
    [Parameter(Mandatory = $true)][string]$ProjectPath,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [switch]$CreateProject
)
$ErrorActionPreference = 'Stop'
$commit = '9750dadc592f421c356bf0376c73e61abbe323e3'
function Invoke-RuntimeEditor([string[]]$Arguments, [string]$Log) {
    foreach ($argument in $Arguments) { if ($argument.Contains('"') -or $argument.Contains("`n") -or $argument.Contains("`r")) { throw 'Unsupported quote/newline in argument.' } }
    $quoted = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $editorProcess = Start-Process -FilePath $Editor -ArgumentList $quoted -WindowStyle Hidden -PassThru
    try { $editorProcess.WaitForExit(); $result = $editorProcess.ExitCode } finally { $editorProcess.Dispose() }
    if ($result -ne 0) { throw "Runtime support Editor failed ($result); log: $Log" }
}
$Editor = (Resolve-Path -LiteralPath $Editor).Path
$ProjectPath = [IO.Path]::GetFullPath($ProjectPath)
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $OutputPath) { throw 'Runtime OutputPath must be new.' }
if ($CreateProject) {
    if (Test-Path -LiteralPath $ProjectPath) { throw '-CreateProject requires a new ProjectPath.' }
    New-Item -ItemType Directory -Path (Split-Path -Parent $ProjectPath) -Force | Out-Null
    $createLog = $ProjectPath + '.create.log'
    Invoke-RuntimeEditor @('-batchmode', '-nographics', '-quit', '-createProject', $ProjectPath, '-buildTarget', 'Win64', '-logFile', $createLog) $createLog
    $newManifestPath = Join-Path $ProjectPath 'Packages/manifest.json'
    # Windows PowerShell 5.1 compatible (no -AsHashtable).
    $newManifest = Get-Content -LiteralPath $newManifestPath -Raw | ConvertFrom-Json
    foreach ($pair in @(@('com.vrmc.gltf', 'UniGLTF'), @('com.vrmc.univrm', 'VRM'), @('com.vrmc.vrm', 'VRM10'))) {
        $newManifest.dependencies | Add-Member -NotePropertyName $pair[0] -NotePropertyValue "https://github.com/vrm-c/UniVRM.git?path=/Packages/$($pair[1])#$commit" -Force
    }
    [IO.File]::WriteAllText($newManifestPath, ($newManifest | ConvertTo-Json -Depth 20))
}
$ProjectPath = (Resolve-Path -LiteralPath $ProjectPath).Path
if ((Get-Content -LiteralPath (Join-Path $ProjectPath 'ProjectSettings/ProjectVersion.txt') -Raw) -notmatch 'm_EditorVersion: 2022\.3\.8f1(?:\r?\n|$)') { throw 'Verified Unity 2022.3.8f1 project required.' }
foreach ($process in Get-CimInstance Win32_Process -Filter "name='Unity.exe'") {
    if ($process.CommandLine -and $process.CommandLine.Contains($ProjectPath)) { throw 'An Editor already owns this project.' }
}
$manifest = Get-Content -LiteralPath (Join-Path $ProjectPath 'Packages/manifest.json') -Raw | ConvertFrom-Json
foreach ($name in @('com.vrmc.gltf', 'com.vrmc.univrm', 'com.vrmc.vrm')) {
    $packagePath = @{ 'com.vrmc.gltf' = 'UniGLTF'; 'com.vrmc.univrm' = 'VRM'; 'com.vrmc.vrm' = 'VRM10' }[$name]
    if ($manifest.dependencies.$name -cne "https://github.com/vrm-c/UniVRM.git?path=/Packages/$packagePath#$commit") { throw "Pinned official UniVRM dependency missing: $name" }
}
$helper = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../unity-editor/BuildVrmRuntime.cs')).Path
$editorFolder = Join-Path $ProjectPath 'Assets/VrmRuntimeEditor'
New-Item -ItemType Directory -Path $editorFolder -Force | Out-Null
Copy-Item -LiteralPath $helper -Destination (Join-Path $editorFolder 'BuildVrmRuntime.cs')
'{"name":"AvatarVrmRuntimeBuilder.Editor","includePlatforms":["Editor"],"autoReferenced":true}' | Set-Content -LiteralPath (Join-Path $editorFolder 'AvatarVrmRuntimeBuilder.Editor.asmdef') -Encoding utf8
$log = $OutputPath + '.log'
New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null
$arguments = @('-batchmode', '-nographics', '-quit', '-projectPath', $ProjectPath, '-buildTarget', 'Win64', '-executeMethod', 'BuildVrmRuntime.BuildBatch', '-runtimeOutput', $OutputPath, '-logFile', $log)
Invoke-RuntimeEditor $arguments $log
if (-not (Select-String -LiteralPath $log -SimpleMatch 'VRM_RUNTIME_BUILD_SUCCESS' -Quiet)) { throw 'Runtime success marker missing.' }
$licenses = Join-Path $OutputPath 'licenses'
New-Item -ItemType Directory -Path $licenses | Out-Null
$cache = Join-Path $ProjectPath 'Library/PackageCache'
$licenseSources = @{
    'UniGLTF-LICENSE.md' = 'com.vrmc.gltf@*/LICENSE.md'
    'UniVRM-LICENSE.md' = 'com.vrmc.univrm@*/LICENSE.md'
    'MToon-LICENSE' = 'com.vrmc.univrm@*/MToon/LICENSE'
    'Unity.Mathematics-LICENSE.md' = 'com.unity.mathematics@*/LICENSE.md'
}
foreach ($entry in $licenseSources.GetEnumerator()) {
    $matches = @(Get-ChildItem -Path (Join-Path $cache $entry.Value) -File)
    if ($matches.Count -ne 1) { throw "Cannot resolve unique license: $($entry.Key)" }
    Copy-Item -LiteralPath $matches[0].FullName -Destination (Join-Path $licenses $entry.Key)
}
@"
Generic VRM 0.x and 1.0 import support; no avatar assets are included.
UniVRM/UniGLTF/MToon source: https://github.com/vrm-c/UniVRM/tree/$commit
UniVRM version: 0.131.3 (VRM, VRM10 and UniGLTF packages from the same repository; UniVRM-LICENSE.md covers VRM10). Build target: Windows x64, Unity 2022.3.8f1 built-in renderer.
Unity.Mathematics: 1.2.6, https://github.com/Unity-Technologies/Unity.Mathematics
Original third-party license texts are retained in licenses/.
Player assemblies were rebuilt from package source; debug records removed with Unity's bundled Cecil.
Shared Unity built-in shader dependencies are compiled with the licensed Unity Editor.
No runtime or avatar visual acceptance is implied by this structural build.

UniGLTF NativeFile.cs attributes its file reader to Unio:
https://github.com/hadashiA/Unio
License source: https://raw.githubusercontent.com/hadashiA/Unio/master/LICENSE
Copyright (c) 2024 hadashiA

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:
The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
"@ | Set-Content -LiteralPath (Join-Path $OutputPath 'THIRD-PARTY-NOTICES.txt') -Encoding utf8
$forbidden = @([Text.Encoding]::UTF8.GetBytes($env:USERPROFILE), [Text.Encoding]::Unicode.GetBytes($env:USERPROFILE))
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $OutputPath 'managed') -File) {
    if ($file.Extension -ne '.dll') { throw 'Only runtime DLLs may be packaged in managed/.' }
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    foreach ($needle in $forbidden) {
        for ($i = 0; $i -le $bytes.Length - $needle.Length; $i++) {
            if ($bytes[$i] -ne $needle[0]) { continue }
            $match = $true
            for ($j = 1; $j -lt $needle.Length; $j++) { if ($bytes[$i + $j] -ne $needle[$j]) { $match = $false; break } }
            if ($match) { throw "Personal path found in $($file.Name) at offset $i" }
        }
    }
}
Write-Output "Generic runtime support: $OutputPath"
Write-Output "Build log (local only): $log"
