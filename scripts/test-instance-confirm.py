#!/usr/bin/env python3
"""Signs in to a copied test instance with its test Owner and saves its three download storage areas once more.

Jularr remembers which filesystem and folder a download area lives in and stops when that changes (a swapped disk must not receive
downloads). A copy lives in another folder, so its areas are confirmed again: the same settings, saved by the copy itself.
Usage: test-instance-confirm.py <address> <owner.txt>
"""
import http.cookiejar, json, sys, time, urllib.request

address, owner = sys.argv[1].rstrip("/"), dict(line.strip().split("=", 1) for line in open(sys.argv[2]) if "=" in line)
jar = http.cookiejar.CookieJar()
web = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))


def call(method, path, body=None, token=None):
    request = urllib.request.Request(address + path, method=method, data=None if body is None else json.dumps(body).encode(),
                                     headers={"Content-Type": "application/json", **({"RequestVerificationToken": token} if token else {})})
    with web.open(request, timeout=20) as response:
        return response.headers.get("RequestVerificationToken"), json.loads(response.read() or b"null")


for _ in range(60):
    try:
        token, _ = call("GET", "/api/v1/auth")
        break
    except OSError:
        time.sleep(2)
token, _ = call("POST", "/api/v1/auth/login", {"username": owner["user"], "password": owner["password"]}, token)
token, _ = call("GET", "/api/v1/admin/instance/settings")
for area in (1, 2, 3):
    _, settings = call("GET", f"/api/v1/admin/storage/downloads/{area}")
    if not settings.get("incompletePath"):
        continue
    keys = ("incompleteStorageMountId", "incompletePath", "completeStorageMountId", "completePath", "freeSpaceReserveBytes",
            "maxParallelJobs", "maxJobBytes", "maxWorkspaceBytes", "version")
    call("POST", f"/api/v1/admin/storage/downloads/{area}", {**{key: settings.get(key) for key in keys}, "confirmed": True}, token)
print("Download storage of the copy confirmed.")
