#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes the API from this PC to the Raspberry Pi and goes back to the previous version if it does not start.
.EXAMPLE
    powershell -File infra\rpi\deploy.ps1
#>
param(
    [string]$PiHost = 'pi@raspberrypi.local',
    [int]$KeepReleases = 3,
    [int]$ReadySeconds = 240
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$release = Get-Date -Format 'yyyyMMdd-HHmmss'
$publish = Join-Path ([IO.Path]::GetTempPath()) "geowoof-publish-$release"
$ssh = @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=10', $PiHost)

function Invoke-Pi([string]$Command) {
    & ssh @ssh $Command
    if ($LASTEXITCODE -ne 0) { throw "Failed on the Raspberry Pi: $Command" }
}

try {
    Write-Host "== Compiling for linux-arm64 ($release)"
    # Self-contained: the Raspberry Pi needs no .NET. ReadyToRun: starts much faster on a Raspberry Pi 3.
    & dotnet publish (Join-Path $root 'backend\src\GeoWoof.Api\GeoWoof.Api.csproj') `
        --configuration Release --runtime linux-arm64 --self-contained true `
        -p:PublishReadyToRun=true -p:UseAppHost=true `
        --output $publish --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'The compilation failed.' }

    $archive = "$publish.tar.gz"
    & tar -czf $archive -C $publish .
    if ($LASTEXITCODE -ne 0) { throw 'Could not pack the release.' }

    Write-Host "== Uploading $([math]::Round((Get-Item $archive).Length / 1MB)) MB"
    & scp -q -o BatchMode=yes $archive "${PiHost}:/tmp/geowoof-$release.tar.gz"
    if ($LASTEXITCODE -ne 0) { throw 'Could not upload the release.' }

    Write-Host '== Backup of the database before the migrations'
    Invoke-Pi 'sudo install -d -m 700 /var/backups/geowoof/pre-deploy && sudo runuser -u postgres -- pg_dump --format=custom --dbname=geowoof | sudo tee /var/backups/geowoof/pre-deploy/latest.dump > /dev/null'

    Write-Host '== Installing and restarting'
    $target = "/opt/geowoof/releases/$release"
    Invoke-Pi "sudo install -d -m 755 $target && sudo tar -xzf /tmp/geowoof-$release.tar.gz -C $target --no-same-owner && sudo chown -R root:root $target && sudo chmod -R u=rwX,go=rX $target && sudo chmod 755 $target/GeoWoof.Api && rm -f /tmp/geowoof-$release.tar.gz"
    Invoke-Pi "readlink -f /opt/geowoof/current > /tmp/geowoof-previous 2>/dev/null || true; sudo ln -sfn $target /opt/geowoof/current && sudo systemctl restart geowoof-api"

    Write-Host "== Waiting for /health/ready (up to $ReadySeconds s)"
    $deadline = (Get-Date).AddSeconds($ReadySeconds)
    $ready = $false
    while (-not $ready -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
        # Quiet on the Raspberry Pi: in PowerShell 5.1, stderr of a native command stops a script with 'Stop'.
        & ssh @ssh 'curl -fs --max-time 4 -o /dev/null http://127.0.0.1:8080/health/ready > /dev/null 2>&1'
        $ready = $LASTEXITCODE -eq 0
    }

    if (-not $ready) {
        Write-Warning 'The new version is not ready. Going back to the previous one.'
        & ssh @ssh 'sudo journalctl -u geowoof-api -n 30 --no-pager'
        & ssh @ssh 'prev=$(cat /tmp/geowoof-previous 2>/dev/null); if [ -d "$prev" ] && [ "$prev" != "$(readlink -f /opt/geowoof/current)" ]; then sudo ln -sfn "$prev" /opt/geowoof/current; sudo systemctl restart geowoof-api; echo "Back on $prev"; else echo "There is no previous version"; fi'
        throw 'Deployment failed. If it applied migrations, the backup is at /var/backups/geowoof/pre-deploy/latest.dump.'
    }

    Invoke-Pi "cd /opt/geowoof/releases && ls -1t | tail -n +$($KeepReleases + 1) | xargs -r sudo rm -rf"
    Write-Host "== Version $release ready"
}
finally {
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $publish, "$publish.tar.gz"
}
