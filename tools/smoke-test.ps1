# Phase 0 smoke test: talk to the plugin without Unity.
# Opens a control connection, sends hello, prints welcome, pings, then tries a
# scene.request to confirm the not_implemented path, and exits.
#
#   .\tools\smoke-test.ps1                       # localhost, default port
#   .\tools\smoke-test.ps1 -HostName 192.168.1.20 -Code 123456

param(
    [string]$HostName = "127.0.0.1",
    [int]$Port = 41427,
    [string]$Code = "",
    [string]$ManifestOut = (Join-Path $PSScriptRoot "last-manifest.json"),
    [switch]$SaveAssets   # also write each asset to tools/assets/<hex>.bin
)

$ErrorActionPreference = "Stop"

function Send-Frame($stream, [hashtable]$header) {
    $json = [System.Text.Encoding]::UTF8.GetBytes(($header | ConvertTo-Json -Compress))
    $frameLen = 4 + $json.Length
    $buf = New-Object byte[] (8 + $frameLen)
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
$pong = Read-Frame $stream
Write-Host "<- $($pong | Select-Object -ExcludeProperty payload | ConvertTo-Json -Compress)"

Send-Frame $stream @{ t = "scene.request"; seq = (++$seq); textures = "opacity"; meshes = $true }
$stream.ReadTimeout = 120000
$reply = Read-Frame $stream

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
    Write-Host "PASS: hello/welcome, ping/pong, scene.manifest, assets verified"
    exit 0
}
Write-Host "FAIL"
exit 1
