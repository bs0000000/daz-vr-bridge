# Phase 0 smoke test: talk to the plugin without Unity.
# Opens a control connection, sends hello, prints welcome, pings, then tries a
# scene.request to confirm the not_implemented path, and exits.
#
#   .\tools\smoke-test.ps1                       # localhost, default port
#   .\tools\smoke-test.ps1 -HostName 192.168.1.20 -Code 123456

param(
    [string]$HostName = "127.0.0.1",
    [int]$Port = 41427,
    [string]$Code = ""
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
    return ($json | ConvertFrom-Json)
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
Write-Host "<- $($welcome | ConvertTo-Json -Compress)"
if ($welcome.t -ne "welcome") { Write-Host "handshake refused"; exit 1 }

Send-Frame $stream @{ t = "ping"; seq = (++$seq) }
$pong = Read-Frame $stream
Write-Host "<- $($pong | ConvertTo-Json -Compress)"

Send-Frame $stream @{ t = "scene.request"; seq = (++$seq); textures = "opacity" }
$err = Read-Frame $stream
Write-Host "<- $($err | ConvertTo-Json -Compress)"

$client.Close()
if ($pong.t -eq "pong" -and $err.t -eq "error" -and $err.code -eq "not_implemented") {
    Write-Host "PASS: hello/welcome, ping/pong, and not_implemented all behave"
    exit 0
}
Write-Host "FAIL: unexpected replies"
exit 1
