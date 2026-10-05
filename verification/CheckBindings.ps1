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
    if ($patches.Count -ne 3) { throw "Expected 3 patch classes, found $($patches.Count)" }
    $flags = [Reflection.BindingFlags]'Static,NonPublic'
    $declaredTargets = @($assembly.GetType('Apocasaver.AutosaveWritePatch').GetMethod('TargetMethods', $flags).Invoke($null, @()))
    if ($declaredTargets.Count -ne 3) { throw 'Native writer hooks are missing' }
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
    Write-Output 'PASS: 5 native hook targets/signatures and Apocapocket API verified against installed game assemblies'
} finally {
    [AppDomain]::CurrentDomain.remove_AssemblyResolve($resolveAssembly)
}
