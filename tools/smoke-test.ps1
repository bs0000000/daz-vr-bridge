# End-to-end protocol smoke test without Unity. It checks hello/welcome,
# ping/pong, edit state, a complete scene bake, bulk transfer and asset hashes.
#
#   .\tools\smoke-test.ps1                       # localhost, default port
#   .\tools\smoke-test.ps1 -HostName 192.168.1.20 -Code 123456
#
#   NEEDS      Daz Studio running, with the built plugin loaded and the VR Bridge
#              pane started, listening on the port below. PowerShell to run it.
#   NEEDS NOT  Unity or a headset -- this stands in for the client.
#   WHO        Whoever is sitting at the Daz machine. An agent on a Linux
#              worktree has no Daz to point this at and should not try; the
#              check that always runs is `python3 tools/check.py`.
#
# Encryption is not implemented here, so the pane needs encryption turned off
# (or the connection has to be loopback, which may talk in the clear).
# `tools/test-smoke.py` runs this same script against a mock plugin instead,
# which needs no Daz.

param(
    [string]$HostName = "127.0.0.1",
    [int]$Port = 41427,
    [string]$Code = "",
    [string]$ManifestOut = (Join-Path $PSScriptRoot "last-manifest.json"),
    [switch]$SaveAssets,  # also write each asset to tools/assets/<hex>.bin
    [switch]$TestUndo     # also undo and immediately redo the top of Daz's undo stack
)

$ErrorActionPreference = "Stop"

function Send-Frame($stream, [hashtable]$header) {
    $json = [System.Text.Encoding]::UTF8.GetBytes(($header | ConvertTo-Json -Compress))
    $frameLen = 4 + $json.Length
    $buf = New-Object byte[] (4 + $frameLen)
    [BitConverter]::GetBytes([uint32]$frameLen).CopyTo($buf, 0)
    [BitConverter]::GetBytes([uint32]$json.Length).CopyTo($buf, 4)
    $json.CopyTo($buf, 8)
    $stream.Write($buf, 0, $buf.Length)
    $stream.Flush()
}

function Read-Exactly($stream, [int]$count) {
    $buf = New-Object byte[] $count
    $off = 0
    while ($off -lt $count) {
        $n = $stream.Read($buf, $off, $count - $off)
        if ($n -le 0) { throw "connection closed" }
        $off += $n
    }
    return $buf
}

function Read-Frame($stream) {
    $frameLen = [BitConverter]::ToUInt32((Read-Exactly $stream 4), 0)
    $body = Read-Exactly $stream $frameLen
    $headerLen = [BitConverter]::ToUInt32($body, 0)
    $json = [System.Text.Encoding]::UTF8.GetString($body, 4, $headerLen)
    $header = $json | ConvertFrom-Json
    $payloadLen = $frameLen - 4 - $headerLen
    $payload = New-Object byte[] $payloadLen
    if ($payloadLen -gt 0) { [Array]::Copy($body, 4 + $headerLen, $payload, 0, $payloadLen) }
    $header | Add-Member -NotePropertyName payload -NotePropertyValue $payload -Force
    return $header
}

# Broadcasts arrive whenever Daz feels like it -- an undo alone produces edit.state,
# pose.state and node.state -- so anything waiting on a specific reply has to step
# over them instead of mistaking the next frame for its answer.
$script:Unsolicited = @("edit.state", "pose.state", "node.state", "scene.changed", "progress")

function Read-Reply($stream) {
    while ($true) {
        $f = Read-Frame $stream
        if ($script:Unsolicited -notcontains $f.t) { return $f }
        Write-Host "   (broadcast: $($f.t))"
    }
}

function Get-Sha1Hex([byte[]]$bytes) {
    $sha = [System.Security.Cryptography.SHA1]::Create()
    return "sha1:" + (([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace("-", "").ToLowerInvariant())
}

$seq = 0
$client = New-Object System.Net.Sockets.TcpClient
$client.NoDelay = $true
$client.Connect($HostName, $Port)
$stream = $client.GetStream()
$stream.ReadTimeout = 5000
Write-Host "connected to ${HostName}:${Port}"

$hello = @{ t = "hello"; seq = (++$seq); protocol = 1; role = "control"; client = "smoke-test.ps1" }
if ($Code) { $hello.code = $Code }
Send-Frame $stream $hello
$welcome = Read-Frame $stream
Write-Host "<- $($welcome | Select-Object -ExcludeProperty payload | ConvertTo-Json -Compress)"
if ($welcome.t -ne "welcome") { Write-Host "handshake refused"; exit 1 }

Send-Frame $stream @{ t = "ping"; seq = (++$seq) }
$pong = Read-Reply $stream
Write-Host "<- $($pong | Select-Object -ExcludeProperty payload | ConvertTo-Json -Compress)"

# --- undo/redo. The no-op direction is always safe to try: with an empty redo
# stack the plugin must answer ok:false rather than an error, and the reply carries
# the same edit fields welcome did. -TestUndo also pops the real top of the stack
# and pushes it straight back.
if ($null -eq $welcome.edit) {
    Write-Host "FAIL: welcome carried no edit state"
    exit 1
}
Write-Host ("edit state: can_undo={0} ('{1}')  can_redo={2} ('{3}')" -f $welcome.edit.can_undo, $welcome.edit.undo, $welcome.edit.can_redo, $welcome.edit.redo)

function Invoke-Edit($stream, [string]$action) {
    Send-Frame $stream @{ t = "edit.$action"; seq = (++$script:seq) }
    $r = Read-Reply $stream
    if ($r.t -ne "edit.result") { Write-Host "<- $($r | ConvertTo-Json -Compress)"; Write-Host "FAIL: expected edit.result"; exit 1 }
    Write-Host ("<- edit.result {0} ok={1} '{2}'  now can_undo={3} can_redo={4}" -f $r.action, $r.ok, $r.caption, $r.can_undo, $r.can_redo)
    return $r
}

if (-not $welcome.edit.can_redo) {
    $r = Invoke-Edit $stream "redo"
    if ($r.ok) { Write-Host "FAIL: redo reported success with an empty redo stack"; exit 1 }
}

if ($TestUndo -and $welcome.edit.can_undo) {
    $u = Invoke-Edit $stream "undo"
    if (-not $u.ok) { Write-Host "FAIL: undo refused while can_undo was true"; exit 1 }
    $r = Invoke-Edit $stream "redo"
    if (-not $r.ok) { Write-Host "FAIL: could not redo what we just undid -- the scene is one step behind"; exit 1 }
}

Send-Frame $stream @{ t = "scene.request"; seq = (++$seq); textures = "opacity"; meshes = $true }
$stream.ReadTimeout = 120000
$reply = Read-Reply $stream

if ($reply.t -ne "scene.manifest") {
    Write-Host "<- $($reply | ConvertTo-Json -Compress)"
    Write-Host "FAIL: expected scene.manifest"
    exit 1
}

$m = $reply.manifest
$m | ConvertTo-Json -Depth 12 | Set-Content -Path $ManifestOut -Encoding UTF8
Write-Host "<- scene.manifest: $($m.nodes.Count) nodes, saved to $ManifestOut"

$m.nodes | Group-Object type | ForEach-Object { Write-Host ("   {0,-9} {1}" -f $_.Name, $_.Count) }

$figures = @($m.nodes | Where-Object { $_.type -eq "figure" })
foreach ($fig in $figures) {
    $bones = $fig.skeleton.bones
    Write-Host ""
    Write-Host "figure '$($fig.label)'  rig=$($fig.rig)  bones=$($bones.Count)  asset=$($fig.asset_id)"
    $orders = $bones | Group-Object rot_order | ForEach-Object { "$($_.Name)x$($_.Count)" }
    Write-Host "   rotation orders: $($orders -join ', ')"
    Write-Host "   mesh: $($fig.vertices) verts, $($fig.triangles) tris"
}

foreach ($cam in @($m.nodes | Where-Object { $_.type -eq "camera" })) {
    $nomH = 2 * [math]::Atan($cam.frame_width_mm / (2 * $cam.focal_mm)) * 180 / [math]::PI
    Write-Host ("camera '{0}': focal={1} mm  frame_w={2} mm  aspect={3:F3} ({4})  Daz getFieldOfView()={5}  (nominal 2*atan(frame/2f) = {6:F2} deg)" -f $cam.label, $cam.focal_mm, $cam.frame_width_mm, $cam.aspect, ($cam.render_px -join "x"), $cam.fov, $nomH)
    if ($null -ne $cam.focal_point) {
        $p = @($cam.transform.pos); $fp = @($cam.focal_point)
        $dir = @(([double]$fp[0] - [double]$p[0]), ([double]$fp[1] - [double]$p[1]), ([double]$fp[2] - [double]$p[2]))
        $len = [math]::Sqrt(($dir[0] * $dir[0]) + ($dir[1] * $dir[1]) + ($dir[2] * $dir[2]))
        $dir = @(($dir[0] / $len), ($dir[1] / $len), ($dir[2] / $len))
        Write-Host ("   pos=({0:F1},{1:F1},{2:F1})  rot_order={3} rot_deg=({4})  view dir (focal_point - pos)=({5:F3},{6:F3},{7:F3})  focal_distance={8:F1}" -f [double]$p[0], [double]$p[1], [double]$p[2], $cam.rot_order, (($cam.rot_deg | ForEach-Object { "{0:F2}" -f $_ }) -join ","), $dir[0], $dir[1], $dir[2], $cam.focal_distance)
        foreach ($ax in "x", "y", "z") {
            $a = @($cam.axes.$ax)
            $dot = ($dir[0] * [double]$a[0]) + ($dir[1] * [double]$a[1]) + ($dir[2] * [double]$a[2])
            Write-Host ("   axis {0}=({1,6:F3},{2,6:F3},{3,6:F3})  dot(view,axis)={4,6:F3}" -f $ax, [double]$a[0], [double]$a[1], [double]$a[2], $dot)
        }
    }
}

# --- assets over a bulk connection
$assets = @($m.assets)
Write-Host ""
Write-Host "bake: $($m.bake.ms) ms, $($assets.Count) assets, $([math]::Round($m.bake.asset_bytes / 1MB, 1)) MB"
$assetsOk = $true
if ($assets.Count -gt 0) {
    $bulk = New-Object System.Net.Sockets.TcpClient
    $bulk.Connect($HostName, $Port)
    $bs = $bulk.GetStream()
    $bs.ReadTimeout = 120000
    $bhello = @{ t = "hello"; seq = 1; protocol = 1; role = "bulk"; client = "smoke-test.ps1"; session = $welcome.session }
    if ($Code) { $bhello.code = $Code }
    Send-Frame $bs $bhello
    $bw = Read-Frame $bs
    if ($bw.t -ne "welcome") { Write-Host "<- $($bw | ConvertTo-Json -Compress)"; Write-Host "FAIL: bulk handshake"; exit 1 }

    Send-Frame $bs @{ t = "asset.request"; seq = 2; hashes = @($assets | ForEach-Object { $_.hash }) }
    foreach ($a in $assets) {
        $d = Read-Frame $bs
        if ($d.t -ne "asset.data") { Write-Host "   <- $($d.t) $($d.code) $($d.msg)"; $assetsOk = $false; continue }
        $ok = (Get-Sha1Hex $d.payload) -eq $d.hash
        if (-not $ok) { $assetsOk = $false }
        if ($SaveAssets) {
            $dir = Join-Path $PSScriptRoot "assets"
            if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
            [IO.File]::WriteAllBytes((Join-Path $dir ($d.hash.Substring(5) + ".bin")), $d.payload)
        }
        Write-Host ("   {0,-9} {1,10:N0} bytes  {2}  {3}" -f $d.kind, $d.payload.Length, $d.hash.Substring(0, 13), $(if ($ok) { "hash ok" } else { "HASH MISMATCH" }))
    }
    $bulk.Close()
}
$client.Close()

if ($pong.t -eq "pong" -and $assetsOk) {
    Write-Host ""
    Write-Host "PASS: hello/welcome, ping/pong, edit state, scene.manifest, assets verified"
    exit 0
}
Write-Host "FAIL"
exit 1
