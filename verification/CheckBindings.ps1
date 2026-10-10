param(
    [string]$GameDir = 'E:\SteamLibrary\steamapps\common\Apocalypter',
    [string]$PluginDll = (Join-Path $PSScriptRoot '..\bin\Release\Apocasaver.dll')
)
$ErrorActionPreference = 'Stop'
$managedDir = Join-Path $GameDir 'Apocalypter_Data\Managed'
$coreDir = Join-Path $GameDir 'BepInEx\core'
$resolveAssembly = [ResolveEventHandler] {
    param($sender, $eventArgs)
    $dependency = ([Reflection.AssemblyName]$eventArgs.Name).Name + '.dll'
    foreach ($baseDir in @($managedDir, $coreDir)) {
        $candidate = Join-Path $baseDir $dependency
        if (Test-Path -LiteralPath $candidate) { return [Reflection.Assembly]::LoadFrom($candidate) }
    }
    return $null
}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolveAssembly)
try {
    $harmonyAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $coreDir '0Harmony.dll'))
    $assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $PluginDll))
    $patches = @($assembly.GetTypes() | Where-Object { $_.IsDefined($harmonyAssembly.GetType('HarmonyLib.HarmonyPatch'), $false) })
    if ($patches.Count -ne 7) { throw "Expected 7 patch classes, found $($patches.Count)" }
    $flags = [Reflection.BindingFlags]'Static,NonPublic'
    $declaredTargets = @($assembly.GetType('Apocasaver.AutosaveWritePatch').GetMethod('TargetMethods', $flags).Invoke($null, @()))
    if ($declaredTargets.Count -ne 3) { throw 'Native writer hooks are missing' }
    foreach ($patchName in @('Apocasaver.SaveActionPatch','Apocasaver.SaveLoadKeyPatch')) {
        $extraTargets = @($assembly.GetType($patchName).GetMethod('TargetMethods', $flags).Invoke($null, @()))
        if ($extraTargets.Count -ne 2 -or ($extraTargets | Where-Object { $null -eq $_ -or $null -eq $_.GetMethodBody() })) { throw "Save/load key targets missing: $patchName" }
    }
    $commitTarget = $assembly.GetType('Apocasaver.SaveFilePatch').GetMethod('TargetMethod', $flags).Invoke($null, @())
    if ($commitTarget.Name -ne 'CommitBackup' -or $commitTarget.GetParameters().Count -ne 1) { throw 'Staged file mirroring signature mismatch' }
    $firstpass = [Reflection.Assembly]::LoadFrom((Join-Path $managedDir 'Assembly-CSharp-firstpass.dll'))
    foreach ($fieldName in @('cache','dirty','syncWithFile')) {
        if ($null -eq $firstpass.GetType('ES3File').GetField($fieldName, [Reflection.BindingFlags]'Instance,NonPublic')) { throw "Cache field missing: $fieldName" }
    }
    $runtimeTypes = @(
        @('PlayMaker', 'HutongGames.PlayMaker.Fsm', 'ProcessEvent'),
        @('PlayMaker', 'HutongGames.PlayMaker.Fsm', 'EnterState'),
        @('Assembly-CSharp-firstpass', 'ES3PlayMaker.SaveAll', 'Enter'),
        @('Assembly-CSharp-firstpass', 'ES3PlayMaker.Save', 'Enter'),
        @('Assembly-CSharp-firstpass', 'ES3PlayMaker.StoreCachedFile', 'Enter')
    )
    foreach ($binding in $runtimeTypes) {
        $runtimeAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $managedDir ($binding[0] + '.dll')))
        $method = [HarmonyLib.AccessTools]::Method($runtimeAssembly.GetType($binding[1]), $binding[2])
        if ($null -eq $method -or $null -eq $method.GetMethodBody()) { throw "Native method unavailable: $($binding[1]).$($binding[2])" }
        if ($binding[2] -eq 'Enter' -and -not $declaredTargets.Contains($method)) { throw "Writer hook target mismatch: $($binding[1])" }
        if ($binding[2] -eq 'EnterState') {
            $postfix = $assembly.GetType('Apocasaver.AutosaveStatePatch').GetMethod('Postfix', $flags)
            if ($postfix.GetParameters()[1].ParameterType -ne $method.GetParameters()[0].ParameterType) { throw 'State-hook parameter mismatch' }
        }
    }
    $pose = $assembly.GetType('Apocasaver.HandPose')
    if ($null -eq $pose.GetMethod('Get', [type[]]@([string])) -or $null -eq $pose.GetMethod('Set', [type[]]@([string], [string]))) { throw 'Apocapocket pose bridge was broken' }
    $pocketDll = Join-Path $GameDir 'BepInEx\plugins\Apocapocket.dll'
    if (-not (Test-Path -LiteralPath $pocketDll)) { $pocketDll = Join-Path $GameDir 'BepInEx\plugins\Apocapocket\Apocapocket.dll' }
    if (Test-Path -LiteralPath $pocketDll) {
        $pocketAssembly = [Reflection.Assembly]::LoadFrom($pocketDll)
        $viewType = $assembly.GetType('Apocasaver.PocketInventoryView')
        $constructor = $viewType.GetConstructor([Reflection.BindingFlags]'Instance,NonPublic', $null, [type[]]@([type]), $null)
        [void]$constructor.Invoke([object[]]@($pocketAssembly.GetType('Apocapocket.Runner', $true)))
        $enabled = $pocketAssembly.GetType('Apocapocket.Plugin', $true).GetField('Enabled', [Reflection.BindingFlags]'Static,NonPublic')
        if ($null -eq $enabled) { throw 'Apocapocket enabled setting is unavailable' }
        Write-Output 'PASS: installed Apocapocket inventory ownership/operation/save bindings verified'
    }
    $patrolDll = Join-Path $GameDir 'BepInEx\plugins\Apocapatrol\Apocapatrol.dll'
    if (Test-Path -LiteralPath $patrolDll) {
        $patrol = [Reflection.Assembly]::LoadFrom($patrolDll)
        $store = $patrol.GetType('Apocapatrol.PatrolPersistence', $true)
        foreach ($fieldName in @('_pendingSlot','_pendingData','_flushAt','_lastState')) {
            if ($null -eq $store.GetField($fieldName, [Reflection.BindingFlags]'Static,NonPublic')) { throw "Patrol field missing: $fieldName" }
        }
        foreach ($methodName in @('Save','FlushPending','Deserialize','TryPath')) {
            if ($null -eq $store.GetMethod($methodName, [Reflection.BindingFlags]'Static,NonPublic')) { throw "Patrol method missing: $methodName" }
        }
        Write-Output 'PASS: installed Apocapatrol persistence/sidecar bindings verified'
    }
    Write-Output 'PASS: native save/load/mirroring/cache bindings and companion APIs verified against installed assemblies'
} finally {
    [AppDomain]::CurrentDomain.remove_AssemblyResolve($resolveAssembly)
}
