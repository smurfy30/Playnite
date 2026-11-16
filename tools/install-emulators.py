"""Install portable emulators into E:/Emulators/<Name> (no admin, no Program Files).

  python tools/install-emulators.py            install anything missing
  python tools/install-emulators.py --force    reinstall (keeps user/config folders untouched only if
                                               the emulator stores them outside the archive's files)

Each emulator gets its "portable mode" marker so config/saves live next to the exe.
URLs are resolved at run time from each project's release feed; see RESOLVERS.
"""
import json, os, re, shutil, subprocess, sys, urllib.request

ROOT = r"E:\Emulators"
DL = os.path.join(ROOT, "_downloads")
SEVENZIP = r"C:\Program Files\7-Zip\7z.exe"
UA = {"User-Agent": "Mozilla/5.0 (ledger emulator installer)"}


def get(url):
    return urllib.request.urlopen(urllib.request.Request(url, headers=UA)).read()


def gh_asset(repo, rx, latest=True):
    url = f"https://api.github.com/repos/{repo}/releases/latest" if latest else f"https://api.github.com/repos/{repo}/releases?per_page=5"
    data = json.loads(get(url))
    rels = [data] if latest else data
    for r in rels:
        for a in r["assets"]:
            if re.search(rx, a["name"], re.I):
                return r["tag_name"], a["browser_download_url"]
    raise RuntimeError(f"no asset matching {rx} in {repo}")


def ppsspp():
    html = get("https://www.ppsspp.org/download/").decode("utf-8", "replace")
    path = re.search(r"/files/([0-9_]+)/ppsspp_win\.zip", html)
    return path.group(1).replace("_", "."), "https://www.ppsspp.org" + path.group(0)


def dolphin():
    info = json.loads(get("https://dolphin-emu.org/update/latest/beta/"))
    rev = info["shortrev"]
    return rev, f"https://dl.dolphin-emu.org/releases/{rev}/dolphin-{rev}-x64.7z"


def retroarch():
    tag, _ = gh_asset("libretro/RetroArch", r".")
    v = tag.lstrip("v")
    return v, f"https://buildbot.libretro.com/stable/{v}/windows/x86_64/RetroArch.7z"


def eden():
    r = json.loads(get("https://git.eden-emu.dev/api/v1/repos/eden-emu/eden/releases?limit=1"))[0]
    a = next(x for x in r["assets"] if re.search(r"Windows.*amd64.*msvc-standard\.zip$", x["name"]))
    return r["tag_name"], a["browser_download_url"]


# name -> (resolver, portable marker relative to install dir: file or dir)
RESOLVERS = {
    "DuckStation": (lambda: gh_asset("stenzek/duckstation", r"windows-x64-release\.zip$", latest=False), ("file", "portable.txt")),
    "PPSSPP": (ppsspp, None),                       # portable by default (memstick next to exe)
    "Dolphin": (dolphin, ("file", "portable.txt")),
    "Cemu": (lambda: gh_asset("cemu-project/Cemu", r"windows-x64\.zip$"), ("dir", "portable")),
    "melonDS": (lambda: gh_asset("melonDS-emu/melonDS", r"windows-x86_64\.zip$"), None),
    "mGBA": (lambda: gh_asset("mgba-emu/mgba", r"win64\.7z$"), ("file", "portable.ini")),
    "Azahar": (lambda: gh_asset("azahar-emu/azahar", r"windows-msvc-[\d.]+\.zip$"), ("dir", "user")),
    "Eden": (eden, ("dir", "user")),
    "RetroArch": (retroarch, None),                 # portable by default
}


def extract(archive, dest):
    tmp = dest + "._tmp"
    shutil.rmtree(tmp, ignore_errors=True)
    subprocess.run([SEVENZIP, "x", "-y", f"-o{tmp}", archive], check=True, stdout=subprocess.DEVNULL)
    # Flatten a single top-level folder ("RetroArch-Win64/…", "melonDS-1.1/…").
    entries = os.listdir(tmp)
    src = os.path.join(tmp, entries[0]) if len(entries) == 1 and os.path.isdir(os.path.join(tmp, entries[0])) else tmp
    os.makedirs(dest, exist_ok=True)
    for e in os.listdir(src):
        target = os.path.join(dest, e)
        if os.path.isdir(target): shutil.rmtree(target)
        elif os.path.exists(target): os.remove(target)
        shutil.move(os.path.join(src, e), target)
    shutil.rmtree(tmp, ignore_errors=True)


def chdman():
    exe = os.path.join(ROOT, "chdman", "chdman.exe")
    if os.path.exists(exe): return exe
    tag, url = gh_asset("mamedev/mame", r"_x64\.exe$")
    arc = os.path.join(DL, os.path.basename(url))
    if not os.path.exists(arc):
        open(arc, "wb").write(get(url))
    os.makedirs(os.path.dirname(exe), exist_ok=True)
    subprocess.run([SEVENZIP, "e", "-y", f"-o{os.path.dirname(exe)}", arc, "chdman.exe"], check=True, stdout=subprocess.DEVNULL)
    print(f"chdman from MAME {tag}")
    return exe


def main():
    force = "--force" in sys.argv
    os.makedirs(DL, exist_ok=True)
    installed = {}
    for name, (resolve, marker) in RESOLVERS.items():
        dest = os.path.join(ROOT, name)
        if os.path.isdir(dest) and os.listdir(dest) and not force:
            print(f"skip  {name} (already installed)")
            continue
        try:
            version, url = resolve()
            arc = os.path.join(DL, f"{name}-{os.path.basename(url)}")
            if not os.path.exists(arc):
                open(arc, "wb").write(get(url))
            extract(arc, dest)
            if marker:
                kind, rel = marker
                p = os.path.join(dest, rel)
                if kind == "dir": os.makedirs(p, exist_ok=True)
                elif not os.path.exists(p): open(p, "w").close()
            installed[name] = version
            print(f"ok    {name} {version}")
        except Exception as e:
            print(f"FAIL  {name}: {e}")
    print("chdman:", chdman())
    with open(os.path.join(ROOT, "versions.json"), "a", encoding="utf-8") as f:
        f.write(json.dumps({"installed": installed}) + "\n")


if __name__ == "__main__":
    main()
