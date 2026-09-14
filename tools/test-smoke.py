"""Exercise smoke-test framing, unsolicited messages, undo/redo and assets without Daz."""
import hashlib
import json
from pathlib import Path
import shutil
import socket
import struct
import subprocess
import threading


def exact(stream, count):
    result = b""
    while len(result) < count:
        part = stream.recv(count - len(result))
        if not part:
            raise RuntimeError("connection closed")
        result += part
    return result


def read(stream, expected):
    size, = struct.unpack("<I", exact(stream, 4))
    assert 4 < size < 65536, f"Bad frame length {size}; trailing bytes?"
    body = exact(stream, size)
    header_size, = struct.unpack("<I", body[:4])
    header = json.loads(body[4:4 + header_size])
    assert header["t"] == expected, header
    return header


def send(stream, header, payload=b""):
    data = json.dumps(header).encode()
    stream.sendall(struct.pack("<II", 4 + len(data) + len(payload), len(data)) + data + payload)


def test():
    errors = []
    payload = b"known asset bytes"
    digest = "sha1:" + hashlib.sha1(payload).hexdigest()
    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    listener.listen()
    listener.settimeout(20)
    port = listener.getsockname()[1]

    def serve():
        try:
            with listener:
                stream, _ = listener.accept()
                with stream:
                    stream.settimeout(20)
                    read(stream, "hello")
                    edit = dict(can_undo=True, can_redo=False, undo="Test pose", redo="")
                    send(stream, dict(t="welcome", session="test", edit=edit))
                    ping = read(stream, "ping")
                    send(stream, dict(t="edit.state", **edit))
                    send(stream, dict(t="pong", ref_seq=ping["seq"]))
                    for action, ok in [("redo", False), ("undo", True), ("redo", True)]:
                        request = read(stream, "edit." + action)
                        send(stream, dict(t="pose.state", figure="test", bones=[]))
                        send(stream, dict(t="edit.result", ref_seq=request["seq"], action=action, ok=ok, caption="Test pose", **edit))
                    request = read(stream, "scene.request")
                    send(stream, dict(t="scene.changed", reason="nodes"))
                    manifest = dict(nodes=[], assets=[dict(hash=digest)], bake=dict(ms=1, asset_bytes=len(payload)))
                    send(stream, dict(t="scene.manifest", ref_seq=request["seq"], manifest=manifest))
                    bulk, _ = listener.accept()
                    with bulk:
                        bulk.settimeout(20)
                        read(bulk, "hello")
                        send(bulk, dict(t="welcome"))
                        assets = read(bulk, "asset.request")
                        assert assets["hashes"] == [digest]
                        send(bulk, dict(t="asset.data", hash=digest, kind="mesh"), payload)
        except Exception as exc:
            errors.append(exc)

    worker = threading.Thread(target=serve, daemon=True)
    worker.start()
    temp = Path(__file__).resolve().parents[1] / "out" / "smoke-test"
    temp.mkdir(parents=True, exist_ok=True)
    result = subprocess.run([shutil.which("powershell") or "pwsh", "-NoProfile", "-File",
            str(Path(__file__).with_name("smoke-test.ps1")), "-Port", str(port), "-TestUndo",
            "-ManifestOut", str(Path(temp) / "manifest.json")], capture_output=True, text=True, timeout=30)
    worker.join(timeout=2)
    assert not worker.is_alive(), "Mock server did not finish"
    assert not errors, errors
    assert result.returncode == 0, result.stdout + result.stderr
    print(result.stdout)
    print("PASS: mock transport, interleaved broadcasts, undo/redo and asset hash")


if __name__ == "__main__":
    test()
