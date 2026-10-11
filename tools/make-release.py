#!/usr/bin/env python3
"""Build SF6Access and package it into the zip players actually download.

Why this exists: the repository only holds SOURCE. A player needs the compiled
plugin plus its data files, laid out exactly where REFramework.NET looks for
them. GitHub Actions cannot build it (the build needs REFramework.NET.dll out of
the local game install and references/tolk, both gitignored), so the zip has to
be built here and uploaded as a GitHub Release asset.

Layout inside the zip - the player unzips it into the game folder, so the paths
inside must start at reframework/:

    reframework/plugins/managed/SF6Access.dll
    reframework/plugins/managed/SF6Access.lang/*.txt
    reframework/plugins/managed/SF6Access.sounds/*.mp3
    reframework/plugins/managed/dependencies/NAudio*.dll
    reframework/plugins/managed/dependencies/TolkDotNet.dll

Usage:
    python tools/make-release.py                     # build, then package
    python tools/make-release.py --no-build          # package existing build
    python tools/make-release.py --version v0.6.0    # name the zip after a version
    python tools/make-release.py --version v0.6.0 --publish --notes-file notes.md

Options:
    --publish     also create the GitHub Release and upload the zip
    --notes-file  markdown file used as the release description
    --draft       create the release as a draft (nothing public until you edit it)
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
import urllib.error
import urllib.request
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROJ = os.path.join(ROOT, "SF6Access", "SF6Access.csproj")
OUT = os.path.join(ROOT, "SF6Access", "bin", "Release", "net10.0")
RELEASE = os.path.join(ROOT, "release")
STAGE = os.path.join(RELEASE, "stage")

SF6_PATH = r"C:\Program Files (x86)\Steam\steamapps\common\Street Fighter 6"
REPO = "vkvk86/sf6Access"

# The SDK lives in the user profile (installed there so no admin was needed); the dotnet.exe on
# PATH (C:\Program Files\dotnet) is runtime-only and cannot build net10.0.
DOTNET = os.environ.get("DOTNET_EXE") or (
    r"C:\Users\jl\.dotnet\dotnet.exe"
    if os.path.exists(r"C:\Users\jl\.dotnet\dotnet.exe") else "dotnet"
)


def run(cmd, **kw):
    print("  $", " ".join(cmd))
    r = subprocess.run(cmd, capture_output=True, text=True, **kw)
    if r.stdout:
        print(r.stdout.rstrip()[:2000])
    if r.returncode != 0:
        print(r.stderr[-2000:])
        sys.exit(r.returncode)
    return r


def build():
    print("== building (Release) ==")
    r = subprocess.run(
        [DOTNET, "build", "-c", "Release", "-p:SF6Path=%s" % SF6_PATH, PROJ],
        capture_output=True, text=True,
    )
    # The build copies into the game folder too; only hard errors matter here.
    bad = [l for l in r.stdout.splitlines() if " error " in l]
    if r.returncode != 0 or bad:
        print("\n".join(bad[:20]) or r.stdout[-2000:])
        sys.exit(1)
    print("  build ok")


def package(version):
    dll = os.path.join(OUT, "SF6Access.dll")
    if not os.path.exists(dll):
        sys.exit("no build output at " + dll)

    if os.path.exists(STAGE):
        shutil.rmtree(STAGE)
    managed = os.path.join(STAGE, "reframework", "plugins", "managed")
    os.makedirs(os.path.join(managed, "SF6Access.lang"))
    os.makedirs(os.path.join(managed, "SF6Access.sounds"))
    os.makedirs(os.path.join(managed, "dependencies"))

    shutil.copy2(dll, os.path.join(managed, "SF6Access.dll"))

    n_lang = 0
    for name in sorted(os.listdir(os.path.join(ROOT, "SF6Access", "lang"))):
        if not name.endswith(".txt") or ".bak" in name:
            continue
        shutil.copy2(os.path.join(ROOT, "SF6Access", "lang", name),
                     os.path.join(managed, "SF6Access.lang", name))
        n_lang += 1

    n_snd = 0
    for name in sorted(os.listdir(os.path.join(ROOT, "SF6Access", "sounds"))):
        shutil.copy2(os.path.join(ROOT, "SF6Access", "sounds", name),
                     os.path.join(managed, "SF6Access.sounds", name))
        n_snd += 1

    deps = os.path.join(managed, "dependencies")
    n_dep = 0
    for name in sorted(os.listdir(OUT)):
        if name.startswith("NAudio") and name.endswith(".dll"):
            shutil.copy2(os.path.join(OUT, name), os.path.join(deps, name))
            n_dep += 1
    tolk = os.path.join(ROOT, "references", "tolk", "TolkDotNet.dll")
    if os.path.exists(tolk):
        shutil.copy2(tolk, os.path.join(deps, "TolkDotNet.dll"))
        n_dep += 1
    else:
        print("  WARNING: references/tolk/TolkDotNet.dll missing - screen reader output "
              "will not work for players. (It is gitignored; it must be copied by hand.)")

    os.makedirs(RELEASE, exist_ok=True)
    zips = []
    base = "SF6Access-%s.zip" % version.lstrip("v") if version else "SF6Access.zip"
    for target in [os.path.join(RELEASE, "SF6Access.zip"), os.path.join(RELEASE, base)]:
        with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as z:
            for dirpath, _, files in os.walk(STAGE):
                for f in sorted(files):
                    full = os.path.join(dirpath, f)
                    z.write(full, os.path.relpath(full, STAGE))
        zips.append(target)

    print("== packaged ==")
    print("  dll 1, lang %d, sounds %d, deps %d" % (n_lang, n_snd, n_dep))
    for z in zips:
        print("  %s  %.1f MB" % (z, os.path.getsize(z) / 1e6))
    return zips[-1]


def github_token():
    """Read the PAT out of Git Credential Manager (stored once, reused forever)."""
    p = subprocess.run(
        ["git", "credential-manager", "get"],
        input="protocol=https\nhost=github.com\n\n",
        capture_output=True, text=True,
    )
    for line in p.stdout.splitlines():
        if line.startswith("password="):
            return line.split("=", 1)[1].strip()
    sys.exit("no GitHub token in the credential manager; run _ghlogin2.sh first")


def api(path, token, data=None, method=None):
    url = "https://api.github.com" + path
    body = json.dumps(data).encode() if data is not None else None
    req = urllib.request.Request(url, data=body, method=method)
    req.add_header("Authorization", "token " + token)
    req.add_header("Accept", "application/vnd.github+json")
    if body:
        req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return json.loads(r.read().decode())
    except urllib.error.HTTPError as e:
        sys.exit("GitHub API %s %s -> %s\n%s" % (method or "GET", path, e.code,
                                                 e.read().decode()[:800]))


def publish(zip_path, version, notes, draft):
    token = github_token()
    print("== creating release %s ==" % version)
    rel = api("/repos/%s/releases" % REPO, token, {
        "tag_name": version,
        "name": version,
        "body": notes or "",
        "draft": draft,
        "prerelease": False,
    })
    rid = rel["id"]
    with open(zip_path, "rb") as fh:
        blob = fh.read()
    print("== uploading %s (%.1f MB) ==" % (os.path.basename(zip_path), len(blob) / 1e6))
    req = urllib.request.Request(
        "https://uploads.github.com/repos/%s/releases/%d/assets?name=SF6Access.zip" % (REPO, rid),
        data=blob, method="POST",
    )
    req.add_header("Authorization", "token " + token)
    req.add_header("Content-Type", "application/zip")
    with urllib.request.urlopen(req, timeout=600) as r:
        asset = json.loads(r.read().decode())
    print("== published ==")
    print("  release :", rel["html_url"])
    print("  download:", "https://github.com/%s/releases/latest/download/SF6Access.zip" % REPO)
    print("  size    : %.1f MB" % (asset["size"] / 1e6))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", default="")
    ap.add_argument("--no-build", action="store_true")
    ap.add_argument("--publish", action="store_true")
    ap.add_argument("--draft", action="store_true")
    ap.add_argument("--notes-file", default="")
    a = ap.parse_args()

    if not a.no_build:
        build()
    zip_path = package(a.version)

    if a.publish:
        if not a.version:
            sys.exit("--publish needs --version")
        notes = ""
        if a.notes_file and os.path.exists(a.notes_file):
            notes = open(a.notes_file, encoding="utf-8").read()
        publish(zip_path, a.version, notes, a.draft)


if __name__ == "__main__":
    main()
