"""The plan format the model writes, and how two plans are compared.

A plan is one line of steps joined by " ; ":

    open <app>                  focus <app>     close <app>     min <app>     max <app>
    snap <layout> [<app>, ...]  layout: side grid left right top bottom full
                                with no apps: the apps named since the previous snap
    desktop                     show the desktop (minimize everything)
    media play|next|prev|stop
    set dark|wifi|bluetooth|mute on|off|toggle
    set volume|brightness <0-100>|up|down
    settings <page>             a Settings page
    folder <name>               downloads, documents, recycle bin, ...
    url <address>               a website
    closeall [except <app>, ...]
    shot  lock  sleep
    none                        not a request: leave the search alone

Apps and Settings pages are written as they were typed (typos too); the engine finds what
they mean. Here both plans are resolved against apps.json before they are compared.
"""

import difflib
import json
import pathlib
import re

HERE = pathlib.Path(__file__).parent
CATALOG = json.loads((HERE / "apps.json").read_text(encoding="utf-8"))

LAYOUTS = {"side", "grid", "left", "right", "top", "bottom", "full"}
APP_VERBS = {"open", "focus", "close", "min", "max"}
# One of these alone already has its own row in the search results.
PLAIN = {"open", "focus", "settings", "folder", "url"}


def _names(groups):
    out = {}
    for g in groups:
        for s in g:
            out.setdefault(s.lower(), g[0].lower())
    return out


APP_NAMES = _names(CATALOG["apps"] + CATALOG["holdout_apps"])
CANON_APPS = sorted({g[0].lower() for g in CATALOG["apps"] + CATALOG["holdout_apps"]})
SETTING_NAMES = _names(CATALOG["settings"])
FOLDER_NAMES = {s: k for k, v in CATALOG["folders"].items() for s in [k, *v]}


def _bare(s):
    s = s.strip().lower()
    s = re.sub(r"^(the|my|a) ", "", s)
    s = re.sub(r" (app|apps|application|window|windows|program|settings|setting|page)$", "", s)
    return s.strip()


def _words_start(name, words):
    parts = [p for p in re.split(r"[^\w]+", name) if p]
    return all(any(p.startswith(w) for p in parts) for w in words)


def resolve(mention, names, canon):
    """What a typed name means: exactly, by its beginning, by the start of its words, or by
    a close spelling. Unknown names stay as typed."""
    m = _bare(mention)
    if m in names:
        return names[m]
    starts = [c for c in canon if c.startswith(m)]
    if len(m) >= 2 and starts:
        return min(starts, key=len)
    words = m.split()
    if len(m) >= 3:
        hits = [c for c in canon if _words_start(c, words)]
        if hits:
            return min(hits, key=len)
    close = difflib.get_close_matches(m, list(names), n=1, cutoff=0.8)
    return names[close[0]] if close else m


def app(m):
    return resolve(m, APP_NAMES, CANON_APPS)


def setting(m):
    return resolve(m, SETTING_NAMES, sorted(set(SETTING_NAMES.values())))


def parse(line):
    """The steps of `line`, resolved, with bare snaps filled in. Raises ValueError if it is not
    a plan."""
    line = line.strip()
    if line == "none":
        return []
    steps = []
    since = []  # apps named since the previous snap
    for part in line.split(" ; "):
        verb, _, rest = part.strip().partition(" ")
        rest = rest.strip()
        if verb in APP_VERBS:
            if not rest:
                raise ValueError(part)
            a = app(rest)
            since.append(a)
            steps.append((verb, a))
        elif verb == "snap":
            layout, _, apps = rest.partition(" ")
            if layout not in LAYOUTS:
                raise ValueError(part)
            names = [app(a) for a in apps.split(",") if a.strip()] or list(dict.fromkeys(since))
            if not names:
                raise ValueError(part)
            since = []
            if layout == "full" and len(names) == 1:
                steps.append(("max", names[0]))
            else:
                steps.append(("snap", layout, tuple(names)))
        elif verb == "media" and rest in {"play", "next", "prev", "stop"}:
            steps.append((verb, rest))
        elif verb == "set":
            what, _, value = rest.partition(" ")
            ok = (what in {"dark", "wifi", "bluetooth", "mute"} and value in {"on", "off", "toggle"}) or (
                what in {"volume", "brightness"} and (value in {"up", "down"} or value.isdigit())
            )
            if not ok:
                raise ValueError(part)
            steps.append((verb, what, value))
        elif verb == "settings" and rest:
            steps.append((verb, setting(rest)))
        elif verb == "folder" and rest:
            steps.append((verb, FOLDER_NAMES.get(_bare(rest), _bare(rest))))
        elif verb == "url" and rest:
            steps.append((verb, re.sub(r"^(https?://)?(www\.)?", "", rest.lower()).rstrip("/")))
        elif verb == "closeall":
            if rest and not rest.startswith("except "):
                raise ValueError(part)
            keep = tuple(sorted(app(a) for a in rest[7:].split(",") if a.strip()))
            steps.append((verb, keep))
        elif verb in {"desktop", "shot", "lock", "sleep"} and not rest:
            steps.append((verb,))
        else:
            raise ValueError(part)
    return steps


def is_row(steps):
    """Would the plan get its own row? One plain step ("open notepad") has one already."""
    return len(steps) > 1 or (len(steps) == 1 and steps[0][0] not in PLAIN)


def same(gold, pred):
    """`pred` does what `gold` asks. "toggle" accepts either state; one plain step is no row."""
    if not is_row(gold) and not is_row(pred):
        return True
    if len(gold) != len(pred):
        return False
    for g, p in zip(gold, pred):
        if g[0] == "set" and p[0] == "set" and g[1] == p[1] and g[2] == "toggle" and p[2] in {"on", "off", "toggle"}:
            continue
        if g != p:
            return False
    return True
