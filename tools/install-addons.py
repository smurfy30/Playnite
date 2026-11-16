"""Install Playnite add-ons from the official add-on API into a user data dir.

Usage: python tools/install-addons.py <ExtensionsDir> <addonId-or-prefix>...

Playnite must be closed. Picks the newest package compatible with SDK_VERSION.
Prefixes are resolved against the full add-on list, since many ids carry a GUID suffix.
"""
import io
import json
import os
import sys
import urllib.request
import zipfile

SDK_VERSION = (6, 18, 0)  # PlayniteSDK/Playnite.SDK.csproj <Version>
API = "https://api.playnite.link/api"


def get(url):
    req = urllib.request.Request(url, headers={"User-Agent": "Playnite"})
    return urllib.request.urlopen(req).read()


def ver(s):
    try:
        return tuple(int(x) for x in s.strip("'\"").split(".")[:3])
    except ValueError:
        return (0,)


def all_ids():
    ids = []
    for t in ("GameLibrary", "MetadataProvider", "Generic"):
        ids += [a["addonId"] for a in json.loads(get(f"{API}/addons?type={t}&searchTerm="))["data"]]
    return ids


def main():
    ext_dir, wanted = sys.argv[1], sys.argv[2:]
    known = all_ids()
    for w in wanted:
        matches = [i for i in known if i == w] or [i for i in known if i.startswith(w)]
        if len(matches) != 1:
            print(f"ERR {w}: {len(matches)} matches")
            continue
        aid = matches[0]
        try:
            m = json.loads(get(f"{API}/addons/installer?addonId={aid}"))["data"]
            pkgs = [p for p in m["packages"] if ver(p["requiredApiVersion"]) <= SDK_VERSION]
            p = max(pkgs, key=lambda p: ver(p["version"]))
            z = zipfile.ZipFile(io.BytesIO(get(p["packageUrl"])))
            root = os.path.join(ext_dir, aid)
            for n in z.namelist():
                parts = [x for x in n.replace("\\", "/").split("/") if x]
                target = os.path.join(root, *parts)
                if n.endswith(("/", "\\")):
                    os.makedirs(target, exist_ok=True)
                    continue
                os.makedirs(os.path.dirname(target), exist_ok=True)
                with open(target, "wb") as f:
                    f.write(z.read(n))
            print(f"OK  {aid} {p['version']}")
        except Exception as e:  # keep going; report at the end
            print(f"ERR {aid}: {e}")


if __name__ == "__main__":
    main()
