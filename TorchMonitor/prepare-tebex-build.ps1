param([switch]$Restore)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$submodule = Join-Path $root 'TorchUtils'
$patch = Join-Path $PSScriptRoot 'patches\TorchUtils-8d8eadf-current-se.patch'
$expected = '8d8eadf88e0762585f4a5016aef29ecb8165568b'
if (-not (Test-Path $submodule)) { throw 'TorchUtils submodule is missing. Run git submodule update --init first.' }
$head = (git -C $submodule rev-parse HEAD).Trim()
if ($head -ne $expected) { throw "Unexpected TorchUtils revision $head; expected $expected." }
if ($Restore) {
    git -C $submodule checkout -- Utils.Torch/VRageUtils.cs
    if ($LASTEXITCODE -ne 0) { throw 'Failed to restore TorchUtils compatibility file.' }
    Write-Output 'Restored pinned TorchUtils source.'
    exit 0
}
$target = Join-Path $submodule 'Utils.Torch\VRageUtils.cs'
$text = [IO.File]::ReadAllText($target)
if ($text.Contains('self.SendAddGpsRequest(identityId, ref gps, gps.EntityId, playSound);')) {
    Write-Output 'TorchUtils current-Space-Engineers compatibility patch already applied.'
    exit 0
}
if (-not $text.Contains('self.SendAddGps(identityId, ref gps, gps.EntityId, playSound);')) {
    throw 'Pinned TorchUtils source no longer matches the reviewed patch preimage.'
}
git -C $submodule apply --check $patch
if ($LASTEXITCODE -ne 0) { throw 'TorchUtils compatibility patch check failed.' }
git -C $submodule apply $patch
if ($LASTEXITCODE -ne 0) { throw 'TorchUtils compatibility patch failed.' }
Write-Output 'Applied reviewed TorchUtils current-Space-Engineers compatibility patch.'