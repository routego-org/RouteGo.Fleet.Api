# One-command deploy of the Fleet API from your Windows PC to the EC2 server.
# It: packs the code -> copies it to EC2 -> builds -> migrates -> starts -> checks /health.
# The server must already run Docker, the Postgres container and Identity (Fleet checks tokens
# against Identity's public keys).
#
# Usage (PowerShell, from anywhere):
#   .\deploy\ec2\deploy.ps1
#   .\deploy\ec2\deploy.ps1 -HostIp 3.111.217.38      # when the EC2 public IP changed
param(
    [string]$HostIp = "3.111.217.38",
    [string]$KeyPath = "$HOME\Downloads\routego-dev-key.pem",
    [string]$User = "ec2-user"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$archive = Join-Path $env:TEMP "fleet.tar.gz"
$target = "$User@$HostIp"

function Assert-LastExit([string]$step) {
    if ($LASTEXITCODE -ne 0) { throw "FAILED at step: $step (exit code $LASTEXITCODE)" }
}

if (-not (Test-Path $KeyPath)) { throw "Key file not found: $KeyPath  (pass -KeyPath <path to .pem>)" }

Write-Host "1/3 Packing code from $repoRoot" -ForegroundColor Cyan
tar.exe -czf $archive --exclude=bin --exclude=obj --exclude=.git --exclude=.vs --exclude=.env -C $repoRoot .
Assert-LastExit "pack code"

Write-Host "2/3 Copying code to $target" -ForegroundColor Cyan
scp -i $KeyPath -o StrictHostKeyChecking=accept-new $archive "${target}:~/fleet.tar.gz"
Assert-LastExit "copy code"

Write-Host "3/3 Unpacking, building and starting on the server" -ForegroundColor Cyan
$remote = "rm -rf ~/fleet && mkdir -p ~/fleet" +
    " && tar -xzf ~/fleet.tar.gz -C ~/fleet" +
    " && sudo bash ~/fleet/deploy/ec2/start.sh"
ssh -i $KeyPath $target $remote
Assert-LastExit "build and start"

Write-Host "DONE. Fleet API is internal only (the portal calls it as http://fleet:8080/)." -ForegroundColor Green
