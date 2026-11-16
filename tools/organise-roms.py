"""Sort E:/emulator_games into one folder per system and convert disc images to CHD.

  python tools/organise-roms.py [--dry-run]

- PS2 .7z/.zip with an .iso      -> ps2/<name>.chd (chdman createdvd)
- CD images (.cue/.bin)          -> ps1/ or ps2/ by the disc's SYSTEM.CNF (BOOT2 = PS2), as .chd (createcd)
- PS3 archives (PS3_GAME inside) -> ps3/<name>/ extracted (RPCS3 boots folders)
- Already-extracted .iso folders -> converted the same way
Originals are MOVED to _archives/, never deleted. Progress -> organise-roms.log; safe to re-run
(skips anything whose output already exists).
"""
import os, re, shutil, subprocess, sys, time

ROOT = r"E:\emulator_games"
WORK = os.path.join(ROOT, "_work")
ARCH = os.path.join(ROOT, "_archives")
SEVENZIP = r"C:\Program Files\7-Zip\7z.exe"
CHDMAN = r"E:\Emulators\chdman\chdman.exe"
SYSTEMS = ["ps1", "ps2", "ps3", "psp", "gc", "wii", "wiiu", "switch", "3ds", "nds", "gba", "gbc", "gb",
           "n64", "snes", "nes", "genesis", "mastersystem", "dreamcast", "saturn", "arcade"]
DRY = "--dry-run" in sys.argv
LOG = open(os.path.join(ROOT, "organise-roms.log"), "a", encoding="utf-8")


def log(msg):
    line = f"{time.strftime('%H:%M:%S')} {msg}"
    print(line, flush=True)
    LOG.write(line + "\n"); LOG.flush()


def run(args):
    if DRY: log("  would run: " + " ".join(args)); return
    subprocess.run(args, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)


def stem(name):
    return re.sub(r"\.(7z|zip|iso)$", "", name, flags=re.I)


def ps_generation(bin_path):
    """'ps2' if the disc boots with BOOT2 (PS2), 'ps1' if BOOT, by scanning the first 64 MB."""
    with open(bin_path, "rb") as f:
        head = f.read(64 * 1024 * 1024)
    if b"BOOT2" in head: return "ps2"
    if re.search(rb"BOOT\s*=", head): return "ps1"
    return "ps2"


def archive_out(p):
    if DRY: log(f"  would archive {p}"); return
    os.makedirs(ARCH, exist_ok=True)
    shutil.move(p, os.path.join(ARCH, os.path.basename(p)))


def convert_dir(src_dir, name):
    """Find the disc image in an extracted folder and make the CHD. Returns output path."""
    files = [os.path.join(dp, f) for dp, _, fs in os.walk(src_dir) for f in fs]
    iso = next((f for f in files if f.lower().endswith(".iso")), None)
    cue = next((f for f in files if f.lower().endswith(".cue")), None)
    if iso:
        system, mode, src = "ps2", "createdvd", iso
    elif cue:
        bin_ = next((f for f in files if f.lower().endswith(".bin")), None)
        system, mode, src = ps_generation(bin_), "createcd", cue
    else:
        raise RuntimeError("no .iso or .cue found")
    out = os.path.join(ROOT, system, name + ".chd")
    if os.path.exists(out): log(f"  exists {out}"); return out
    log(f"  chdman {mode} -> {system}/{name}.chd")
    run([CHDMAN, mode, "-i", src, "-o", out])
    return out


def main():
    for s in SYSTEMS: os.makedirs(os.path.join(ROOT, s), exist_ok=True)
    for name in sorted(os.listdir(ROOT)):
        p = os.path.join(ROOT, name)
        if name.startswith("_") or name in SYSTEMS or name.endswith((".log", ".out")): continue
        try:
            if os.path.isfile(p) and name.lower().endswith((".7z", ".zip")):
                listing = subprocess.run([SEVENZIP, "l", "-ba", p], capture_output=True, text=True, errors="replace").stdout
                if "PS3_GAME" in listing:
                    dest = os.path.join(ROOT, "ps3", stem(name))
                    if os.path.isdir(dest) or any(stem(name) in d for d in os.listdir(os.path.join(ROOT, "ps3"))):
                        log(f"PS3 {name}: already extracted, archiving original")
                    else:
                        log(f"PS3 {name}: extracting")
                        run([SEVENZIP, "x", "-y", f"-o{os.path.join(ROOT, 'ps3')}", p])
                    archive_out(p)
                    continue
                out_guess = [os.path.join(ROOT, s, stem(name) + ".chd") for s in ("ps1", "ps2")]
                if any(os.path.exists(o) for o in out_guess):
                    log(f"{name}: CHD exists, archiving original"); archive_out(p); continue
                log(f"{name}: extracting")
                work = os.path.join(WORK, stem(name))
                shutil.rmtree(work, ignore_errors=True)
                run([SEVENZIP, "x", "-y", f"-o{work}", p])
                if not DRY: convert_dir(work, stem(name))
                shutil.rmtree(work, ignore_errors=True)
                archive_out(p)
            elif os.path.isdir(p):
                files = [f for _, _, fs in os.walk(p) for f in fs]
                if any(f.lower().endswith((".iso", ".cue")) for f in files):
                    log(f"{name}/: converting extracted image")
                    if not DRY: convert_dir(p, name)
                    archive_out(p)
            log(f"done {name}")
        except subprocess.CalledProcessError as e:
            log(f"FAIL {name}: {e.stderr.decode(errors='replace')[-300:] if e.stderr else e}")
        except Exception as e:
            log(f"FAIL {name}: {e}")
    shutil.rmtree(WORK, ignore_errors=True)
    log("ALL DONE")


if __name__ == "__main__":
    main()
