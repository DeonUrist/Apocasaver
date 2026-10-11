param([string]$GameDir = 'E:\SteamLibrary\steamapps\common\Apocalypter', [switch]$NoMods, [string]$PocketDll)
$ErrorActionPreference = 'Stop'
$taskGame = (Resolve-Path -LiteralPath $GameDir).Path
$taskRepo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$taskRuntime = Join-Path $PSScriptRoot $(if ($NoMods) { 'runtime-vanilla' } else { 'runtime-mods' })
$taskOutput = Join-Path $PSScriptRoot ('results\' + $(if ($NoMods) { 'vanilla-' } else { 'mods-' }) + (Get-Date -Format yyyyMMdd-HHmmss))
dotnet build (Join-Path $taskRepo 'Apocasaver.csproj') -c Release -p:SkipDeploy=true --nologo
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed' }
dotnet build (Join-Path $PSScriptRoot 'NativeVerifier.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Verifier build failed' }
New-Item -ItemType Directory -Path "$taskRuntime\BepInEx\plugins", "$taskRuntime\BepInEx\config", $taskOutput -Force | Out-Null
foreach ($file in @('Apocalypter.exe','UnityPlayer.dll','winhttp.dll','doorstop_config.ini','.doorstop_version')) { Copy-Item -LiteralPath "$taskGame\$file" -Destination $taskRuntime -Force }
Copy-Item -LiteralPath "$taskGame\BepInEx\core" -Destination "$taskRuntime\BepInEx" -Recurse -Force
foreach ($dir in @('Apocalypter_Data','MonoBleedingEdge')) { if (!(Test-Path -LiteralPath "$taskRuntime\$dir")) { New-Item -ItemType Junction -Path "$taskRuntime\$dir" -Target "$taskGame\$dir" | Out-Null } }
Copy-Item -LiteralPath "$taskRepo\bin\Release\Apocasaver.dll" -Destination "$taskRuntime\BepInEx\plugins" -Force
Copy-Item -LiteralPath "$PSScriptRoot\bin\Release\net472\ApocasaverNativeVerifier.dll" -Destination "$taskRuntime\BepInEx\plugins" -Force
if (!$NoMods) {
    foreach ($mod in @('Apocapatrol','Apocapocket')) {
        $taskModDll = if ($mod -eq 'Apocapocket' -and $PocketDll) { (Resolve-Path -LiteralPath $PocketDll).Path } else {
            @("$taskGame\BepInEx\plugins\$mod.dll", "$taskGame\BepInEx\plugins\$mod\$mod.dll") | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        }
        if (!$taskModDll) { throw "Installed $mod not found" }
        Copy-Item -LiteralPath $taskModDll -Destination "$taskRuntime\BepInEx\plugins" -Force
    }
}
@'
[Logging.Console]
Enabled = false
[Logging.Disk]
Enabled = true
LogLevels = All
'@ | Set-Content -LiteralPath "$taskRuntime\BepInEx\config\BepInEx.cfg"
@'
[General]
Autosave enabled = false
Save naming = false
[Saving]
CleanSaves = false
'@ | Set-Content -LiteralPath "$taskRuntime\BepInEx\config\com.denis.apocalypter.apocasaver.cfg"
@'
[General]
Enabled = false
'@ | Set-Content -LiteralPath "$taskRuntime\BepInEx\config\com.denis.apocalypter.apocapatrol.cfg"
@'
[General]
Enabled = false
'@ | Set-Content -LiteralPath "$taskRuntime\BepInEx\config\com.denis.apocalypter.apocapocket.cfg"
$taskArgs = @('-batchmode','-nographics','-logFile',('"' + $taskRuntime + '\unity.log"'),('-save-test-root="' + $taskOutput + '"'))
$taskProcess = Start-Process -FilePath "$taskRuntime\Apocalypter.exe" -WorkingDirectory $taskRuntime -WindowStyle Hidden -ArgumentList $taskArgs -PassThru
try {
    $taskDeadline = (Get-Date).AddMinutes(3)
    while (!$taskProcess.WaitForExit(1000)) { if ((Get-Date) -gt $taskDeadline) { throw 'Native verifier timed out' } }
    $taskReport = Join-Path $taskOutput 'report.txt'
    if (!(Test-Path -LiteralPath $taskReport)) { throw "No report; inspect $taskRuntime\BepInEx\LogOutput.log" }
    Get-Content -LiteralPath $taskReport
    Write-Output "Report: $taskReport"
    if (Select-String -LiteralPath $taskReport -Pattern '^FAIL:') { throw 'Native verifier failed' }
} finally { if (!$taskProcess.HasExited) { Stop-Process -Id $taskProcess.Id } }
