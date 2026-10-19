param(
    [Parameter(Mandatory = $true)][string]$GameDirectory,
    [Parameter(Mandatory = $true)][string]$BepInExCore
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$managed = Join-Path $GameDirectory 'valheim_Data/Managed'
if (!(Test-Path -LiteralPath $managed)) { $managed = Join-Path $GameDirectory 'valheim_server_Data/Managed' }
if (!(Test-Path -LiteralPath $managed)) { throw 'Game Managed directory not found' }
$lib = Join-Path $workspace 'lib'
$refs = Join-Path $workspace '.refs'
New-Item -ItemType Directory -Force $lib, $refs | Out-Null
$gameDlls = @('assembly_valheim.dll','assembly_utils.dll','assembly_guiutils.dll','Splatform.dll',
    'netstandard.dll','System.Memory.dll','System.Runtime.CompilerServices.Unsafe.dll','Unity.TextMeshPro.dll',
    'UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.AnimationModule.dll','UnityEngine.AudioModule.dll',
    'UnityEngine.ImageConversionModule.dll','UnityEngine.IMGUIModule.dll','UnityEngine.InputLegacyModule.dll',
    'UnityEngine.PhysicsModule.dll','UnityEngine.TextRenderingModule.dll','UnityEngine.UI.dll','UnityEngine.UIModule.dll')
foreach ($name in $gameDlls) { Copy-Item -LiteralPath (Join-Path $managed $name) -Destination $lib -Force }
foreach ($name in @('BepInEx.dll','0Harmony.dll','Mono.Cecil.dll')) {
    Copy-Item -LiteralPath (Join-Path $BepInExCore $name) -Destination $lib -Force
}
Copy-Item -LiteralPath (Join-Path $managed 'assembly_valheim.dll') -Destination $refs -Force
# Publicize a reference-only copy with the installed loader's Cecil; never alter
# the game assembly. Keep the original for the runtime-accessibility verifier.
Add-Type -LiteralPath (Join-Path $lib 'Mono.Cecil.dll')
$resolver = [Mono.Cecil.DefaultAssemblyResolver]::new()
$resolver.AddSearchDirectory($managed)
$parameters = [Mono.Cecil.ReaderParameters]::new()
$parameters.AssemblyResolver = $resolver
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $refs 'assembly_valheim.dll'), $parameters)
function Publish-ReferenceType($type) {
    if ($type.IsNested) { $type.IsNestedPublic = $true } else { $type.IsPublic = $true }
    foreach ($field in $type.Fields) { $field.IsPublic = $true }
    foreach ($method in $type.Methods) { $method.IsPublic = $true }
    foreach ($nested in $type.NestedTypes) { Publish-ReferenceType $nested }
}
try {
    foreach ($type in $assembly.MainModule.Types) { Publish-ReferenceType $type }
    $assembly.Write((Join-Path $lib 'assembly_valheim_publicized.dll'))
} finally { $assembly.Dispose(); $resolver.Dispose() }
Write-Output 'Private reference assemblies ready; no game files modified.'
