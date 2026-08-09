param(
    [Parameter(Mandatory = $true)]
    [string]$Executable
)

$ErrorActionPreference = 'Stop'

$resolvedExecutable = (Resolve-Path -LiteralPath $Executable).Path
Start-MpScan -ScanType CustomScan -ScanPath $resolvedExecutable

$defenderStatus = Get-MpComputerStatus
$escapedExecutable = [regex]::Escape($resolvedExecutable)
$matchingDetections = @(Get-MpThreatDetection | Where-Object {
    [string]($_.Resources -join "`n") -match $escapedExecutable
})
$signature = Get-AuthenticodeSignature -LiteralPath $resolvedExecutable
$hash = Get-FileHash -LiteralPath $resolvedExecutable -Algorithm SHA256
$file = Get-Item -LiteralPath $resolvedExecutable

Write-Output "THREAT_COUNT=$($matchingDetections.Count)"
Write-Output "DEFENDER_ENGINE=$($defenderStatus.AMEngineVersion)"
Write-Output "DEFENDER_SIGNATURE=$($defenderStatus.AntivirusSignatureVersion)"
Write-Output "DEFENDER_SIGNATURE_UPDATED=$($defenderStatus.AntivirusSignatureLastUpdated.ToString('o'))"
Write-Output "REALTIME_PROTECTION=$($defenderStatus.RealTimeProtectionEnabled)"
Write-Output "AUTHENTICODE=$($signature.Status)"
Write-Output "FILE_SIZE=$($file.Length)"
Write-Output "SHA256=$($hash.Hash)"

if ($matchingDetections.Count -ne 0) {
    throw 'Microsoft Defender reported a detection for the release artifact.'
}
