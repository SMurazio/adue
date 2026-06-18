#requires -Version 5
<#
.SYNOPSIS
    Drive the Godot client's localhost debug control channel (design T3) and read its live state.

.DESCRIPTION
    Connects to the client's 127.0.0.1 TCP control channel (DebugControlChannel.cs / IControlHost.cs)
    and speaks its line-delimited JSON protocol: one `{"cmd":"..."}` request line in, one JSON
    response line out. The channel only exists when the client was started with the
    MMO_DEBUG_CONTROL_PORT environment variable set; this script connects to that same port.

    Default port resolution: -Port arg, else $env:MMO_DEBUG_CONTROL_PORT, else error.

    Each switch maps to one channel command/query. Multiple switches may be combined; they run in a
    fixed, sensible order (state/telemetry/interp/entities queries, then move/stop, then autopilot
    last). With no switch the script just pings the channel to prove it is reachable.

    After -Autopilot the script waits for the run to finish, then summarizes .run/client-frames.csv:
    the worst frames by frameMs and which _Process section (poll/renderState/entities/camera/overlay)
    dominated each, so the residual hitch can be attributed without a human watching the HUD.

.EXAMPLE
    client-control.cmd -Telemetry
    client-control.cmd -State -Entities
    client-control.cmd -Move N -DurationMs 2000
    client-control.cmd -Stop
    client-control.cmd -Autopilot 20
    client-control.cmd -Cmd '{"cmd":"chat","text":"hello"}'
#>
[CmdletBinding()]
param(
    # Channel port. Defaults to $env:MMO_DEBUG_CONTROL_PORT.
    [int]$Port = 0,

    # Channel host. Loopback only; do not point this at a remote machine.
    [string]$ClientHost = '127.0.0.1',

    # Queries.
    [switch]$Telemetry,
    [switch]$Interp,
    [switch]$Entities,
    [switch]$State,

    # Movement commands.
    [ValidateSet('N', 'NE', 'E', 'SE', 'S', 'SW', 'W', 'NW')]
    [string]$Move = '',
    [double]$DurationMs = 0,
    [switch]$Stop,

    # Autopilot: run a scripted movement loop for N seconds, then summarize the frame CSV.
    [double]$Autopilot = 0,
    [ValidateSet('square', 'line', 'zigzag', 'circle')]
    [string]$Pattern = 'square',

    # Raw escape hatch: send a full JSON request line verbatim (e.g. chat / toggle_* / ping).
    [string]$Cmd = '',

    # How many worst frames to list in the autopilot summary.
    [int]$Top = 8
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$framesCsv = Join-Path $root '.run\client-frames.csv'

function Resolve-Port {
    if ($Port -gt 0) { return $Port }
    $envPort = $env:MMO_DEBUG_CONTROL_PORT
    if (-not [string]::IsNullOrWhiteSpace($envPort)) {
        $parsed = 0
        if ([int]::TryParse($envPort.Trim(), [ref]$parsed) -and $parsed -ge 1 -and $parsed -le 65535) {
            return $parsed
        }
    }

    throw "No control port. Pass -Port <n> or set MMO_DEBUG_CONTROL_PORT (the same value the client was started with)."
}

# Sends one JSON request line and returns the single JSON response line as a string.
# Opens a fresh connection per request; the channel handles many short connections fine and this keeps
# the script simple and robust against a half-closed socket between commands.
function Send-Request {
    param([Parameter(Mandatory)][string]$Json)

    $resolvedPort = Resolve-Port
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        # Connect with a timeout so a missing/closed channel fails fast instead of hanging.
        $async = $client.BeginConnect($ClientHost, $resolvedPort, $null, $null)
        if (-not $async.AsyncWaitHandle.WaitOne(3000)) {
            throw "Timed out connecting to control channel at ${ClientHost}:${resolvedPort}. Is the client running with MMO_DEBUG_CONTROL_PORT=$resolvedPort?"
        }

        $client.EndConnect($async)
        $client.NoDelay = $true
        $client.ReceiveTimeout = 5000

        $stream = $client.GetStream()
        $writer = New-Object System.IO.StreamWriter($stream, (New-Object System.Text.UTF8Encoding($false)))
        $writer.NewLine = "`n"
        $writer.AutoFlush = $true
        $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)

        $writer.WriteLine($Json)
        $response = $reader.ReadLine()
        if ($null -eq $response) {
            throw "Control channel closed without responding to: $Json"
        }

        return $response
    }
    catch [System.Net.Sockets.SocketException] {
        throw "Cannot reach control channel at ${ClientHost}:${resolvedPort}: $($_.Exception.Message). Start the client with MMO_DEBUG_CONTROL_PORT set."
    }
    finally {
        $client.Close()
    }
}

# Sends a request and pretty-prints the JSON response, labelled with the command name.
function Invoke-ChannelCommand {
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][string]$Json
    )

    Write-Host ""
    Write-Host "== $Label ==" -ForegroundColor Cyan
    Write-Host "-> $Json" -ForegroundColor DarkGray
    $response = Send-Request -Json $Json
    try {
        $obj = $response | ConvertFrom-Json
        ($obj | ConvertTo-Json -Depth 8)
    }
    catch {
        # Channel guarantees one JSON object per line; if parsing fails, surface the raw text.
        $response
    }
}

# Parses .run/client-frames.csv and prints the worst frames plus the dominant _Process section.
function Show-FrameSummary {
    param([Parameter(Mandatory)][string]$Path)

    Write-Host ""
    Write-Host "== client-frames.csv summary ==" -ForegroundColor Cyan
    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Host "No CSV at $Path (autopilot may not have started or the client lacks write access)." -ForegroundColor Yellow
        return
    }

    $rows = Import-Csv -LiteralPath $Path
    if (-not $rows -or $rows.Count -eq 0) {
        Write-Host "CSV is empty: $Path" -ForegroundColor Yellow
        return
    }

    $sections = 'pollMs', 'renderStateMs', 'entitiesMs', 'cameraMs', 'overlayMs'

    $frames = foreach ($r in $rows) {
        $frameMs = [double]$r.frameMs
        $dominant = $null
        $dominantMs = -1.0
        foreach ($s in $sections) {
            $v = [double]$r.$s
            if ($v -gt $dominantMs) {
                $dominantMs = $v
                $dominant = $s
            }
        }

        [pscustomobject]@{
            elapsedSec   = [double]$r.elapsedSec
            frameMs      = $frameMs
            dominant     = $dominant.Replace('Ms', '')
            dominantMs   = $dominantMs
            poll         = [double]$r.pollMs
            renderState  = [double]$r.renderStateMs
            entities     = [double]$r.entitiesMs
            camera       = [double]$r.cameraMs
            overlay      = [double]$r.overlayMs
            gc0          = [int]$r.gc0
            gc1          = [int]$r.gc1
            gc2          = [int]$r.gc2
        }
    }

    $count = $frames.Count
    $avg = ($frames | Measure-Object -Property frameMs -Average).Average
    $max = ($frames | Measure-Object -Property frameMs -Maximum).Maximum
    $sorted = $frames | Sort-Object frameMs
    $p95 = $sorted[[int][math]::Floor(($count - 1) * 0.95)].frameMs
    $p99 = $sorted[[int][math]::Floor(($count - 1) * 0.99)].frameMs
    $totalGc0 = ($frames | Measure-Object -Property gc0 -Sum).Sum
    $totalGc1 = ($frames | Measure-Object -Property gc1 -Sum).Sum
    $totalGc2 = ($frames | Measure-Object -Property gc2 -Sum).Sum

    Write-Host ("frames={0}  avg={1:0.00}ms  p95={2:0.00}ms  p99={3:0.00}ms  max={4:0.00}ms  gc(0/1/2)={5}/{6}/{7}" -f `
        $count, $avg, $p95, $p99, $max, $totalGc0, $totalGc1, $totalGc2)

    # Which section accounts for the most spike time across the worst frames.
    $byDominant = $frames | Group-Object dominant | Sort-Object Count -Descending
    $blame = ($byDominant | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join '  '
    Write-Host "dominant section per frame (count): $blame"

    $worst = $frames | Sort-Object frameMs -Descending | Select-Object -First $Top
    Write-Host ""
    Write-Host ("Worst {0} frames by frameMs:" -f $worst.Count)
    $worst |
        Select-Object `
            @{ n = 'atSec'; e = { '{0:0.00}' -f $_.elapsedSec } },
            @{ n = 'frameMs'; e = { '{0:0.00}' -f $_.frameMs } },
            @{ n = 'dominant'; e = { $_.dominant } },
            @{ n = 'domMs'; e = { '{0:0.00}' -f $_.dominantMs } },
            @{ n = 'poll'; e = { '{0:0.00}' -f $_.poll } },
            @{ n = 'render'; e = { '{0:0.00}' -f $_.renderState } },
            @{ n = 'entities'; e = { '{0:0.00}' -f $_.entities } },
            @{ n = 'camera'; e = { '{0:0.00}' -f $_.camera } },
            @{ n = 'overlay'; e = { '{0:0.00}' -f $_.overlay } },
            @{ n = 'gc'; e = { "$($_.gc0)/$($_.gc1)/$($_.gc2)" } } |
        Format-Table -AutoSize | Out-String | Write-Host

    Write-Host "Full trace: $Path"
}

# ---- Drive the requested sequence ------------------------------------------------------------

$did = $false

if ($State)     { Invoke-ChannelCommand -Label 'state'     -Json '{"cmd":"state"}';     $did = $true }
if ($Telemetry) { Invoke-ChannelCommand -Label 'telemetry' -Json '{"cmd":"telemetry"}'; $did = $true }
if ($Interp)    { Invoke-ChannelCommand -Label 'interp'    -Json '{"cmd":"interp"}';    $did = $true }
if ($Entities)  { Invoke-ChannelCommand -Label 'entities'  -Json '{"cmd":"entities"}';  $did = $true }

if (-not [string]::IsNullOrWhiteSpace($Move)) {
    $moveJson = if ($DurationMs -gt 0) {
        '{{"cmd":"move","dir":"{0}","durationMs":{1}}}' -f $Move, ([int]$DurationMs)
    } else {
        '{{"cmd":"move","dir":"{0}"}}' -f $Move
    }

    Invoke-ChannelCommand -Label 'move' -Json $moveJson
    $did = $true
}

if ($Stop) { Invoke-ChannelCommand -Label 'stop' -Json '{"cmd":"stop"}'; $did = $true }

if (-not [string]::IsNullOrWhiteSpace($Cmd)) {
    Invoke-ChannelCommand -Label 'raw' -Json $Cmd
    $did = $true
}

if ($Autopilot -gt 0) {
    $durationMs = [int]($Autopilot * 1000)
    $autopilotJson = '{{"cmd":"autopilot","pattern":"{0}","durationMs":{1}}}' -f $Pattern, $durationMs
    Invoke-ChannelCommand -Label 'autopilot (start)' -Json $autopilotJson

    # The autopilot command returns immediately; the run plays out over subsequent client frames and
    # streams rows to the CSV. Wait the requested duration plus a small buffer for the final flush.
    $waitSec = [int][math]::Ceiling($Autopilot) + 2
    Write-Host ""
    Write-Host ("Autopilot running for {0:0}s ({1}); waiting {2}s before reading the CSV..." -f $Autopilot, $Pattern, $waitSec) -ForegroundColor DarkGray
    Start-Sleep -Seconds $waitSec

    Show-FrameSummary -Path $framesCsv
    $did = $true
}

if (-not $did) {
    # No action requested: prove the channel is reachable.
    Invoke-ChannelCommand -Label 'ping' -Json '{"cmd":"ping"}'
}
