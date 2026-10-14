"""Generate training examples: requests in many phrasings, each with the plan it asks for.

    python gen.py [--n 30000] [--seed 7]

Writes data/train.jsonl and data/valid.jsonl ({"q": ..., "plan": ...} per line). Requests in
test.jsonl are left out, and its held-out apps are never used, so the test measures phrasings
and names the model has not seen.
"""

import argparse
import json
import pathlib
import random
import re

from plan import CATALOG, HERE, parse

R = random.Random()

# ---------------------------------------------------------------- names

APPS = [[s.lower() for s in g] for g in CATALOG["apps"]]

# Names outside the catalog, so the model learns to copy whatever name it is given.
OTHER_APPS = """audible evernote trello asana jira miro canva lightshot greenshot sharex handbrake shotcut
kdenlive openshot darktable rawtherapee musescore reaper bandicam teamviewer anydesk parsec moonlight
speccy ccleaner malwarebytes avast norton nordvpn expressvpn mullvad sourcetree gitkraken dbeaver
heidisql tableplus insomnia webstorm rider clion goland eclipse netbeans xampp virtualbox wsl ubuntu
duolingo todoist ticktick spark airmail mailbird franz rambox ferdium pidgin hexchat mumble teamspeak
guilded revolt beeper caprine lunacy penpot pencil2d synfig opentoonz aseprite pyxel tiled ldtk
bitwig ardour lmms renoise cakewalk nuendo cubase protools traktor serato rekordbox virtualdj winamp
aimp clementine strawberry quodlibet mpv potplayer kmplayer mpc-hc smplayer jellyfin emby kodi
stremio popcorn wox flow launcher listary keypirinha ueli raycast fences rainmeter wallpaper engine
lively translucenttb startallback openshell explorerpatcher quicklook peazip bandizip nanazip""".split()
OTHER_APPS += ["logitech g hub", "razer synapse", "msi afterburner", "github desktop", "mongodb compass",
               "vmware workstation", "hyper-v manager", "kali linux", "nvidia broadcast", "visual paradigm",
               "adobe illustrator", "adobe after effects", "adobe xd", "microsoft whiteboard", "power bi",
               "sql server management studio", "azure data studio", "google earth", "apple tv", "amazon music",
               "disney plus", "crunchyroll", "epic privacy browser", "opera gx", "brave nightly", "edge dev"]
_A = "zen flux nova pix quill ora lumen vex sol tide echo mira kilo rune tera arc byte cloud swift note".split()
_B = "ly io pad note forge desk box flow cast hub kit lab mate base ware works deck sync link".split()
_names = random.Random(1)
for a in _A:
    for b in _B:
        if _names.random() < 0.35:
            OTHER_APPS.append(a + b)
HELD = {s.lower() for g in CATALOG["holdout_apps"] for s in g}
OTHER_APPS = [a for a in OTHER_APPS if a not in HELD]

KEYS = "qwertyuiop asdfghjkl zxcvbnm".split()


def near(c):
    for row in KEYS:
        i = row.find(c)
        if i >= 0:
            return row[max(0, i - 1)] if R.random() < 0.5 else row[min(len(row) - 1, i + 1)]
    return c


def typo(w):
    """One slip of the fingers in a longer word."""
    idx = [i for i, c in enumerate(w) if c.isalpha()]
    if len(idx) < 4:
        return w
    i = R.choice(idx[1:-1])
    k = R.random()
    if k < 0.3:
        return w[:i] + w[i + 1] + w[i] + w[i + 2:]
    if k < 0.55:
        return w[:i] + w[i + 1:]
    if k < 0.75:
        return w[:i] + w[i] + w[i:]
    return w[:i] + near(w[i]) + w[i + 1:]


def pick(xs):
    return R.choice(xs)


def a_name():
    """(as written in the request, as written in the plan)."""
    if R.random() < 0.22:
        name = pick(OTHER_APPS)
    else:
        g = pick(APPS)
        name = pick(g) if R.random() < 0.6 else g[0]
    if R.random() < 0.07:
        name = typo(name)
    shown = name
    if R.random() < 0.12:
        shown = name.title() if R.random() < 0.7 else name.upper() if len(name) <= 4 else name.capitalize()
    return shown, name


def apps(n, fr=False):
    out, seen = [], set()
    while len(out) < n:
        s, m = a_name()
        if m in seen:
            continue
        seen.add(m)
        if not fr and R.random() < 0.12:
            s = pick(["the {}", "my {}", "{} app", "the {} app", "{} window", "the {} window"]).format(s)
        out.append((s, m))
    return out


def listing(xs, fr=False):
    xs = list(xs)
    if len(xs) == 1:
        return xs[0]
    if fr:
        last = pick([" et ", ", ", " et puis "]) if len(xs) == 2 else pick([" et ", ", "])
        return ", ".join(xs[:-1]) + last + xs[-1]
    if len(xs) == 2:
        return xs[0] + pick([" and ", " and ", " and ", ", ", " & ", " + ", " with ", " plus ", " and also "]) + xs[1]
    if R.random() < 0.2:
        return " and ".join(xs)
    return ", ".join(xs[:-1]) + pick([" and ", ", and ", ", ", " & "]) + xs[-1]


def polite(q, fr=False):
    if fr:
        return pick(["", "", "", "peux-tu ", "stp "]) + q + pick(["", "", "", " stp", " s'il te plaît", " merci"])
    pre = pick([""] * 9 + ["please ", "can you ", "could you ", "hey ", "ok ", "i want to ", "i'd like to ",
                           "go ahead and ", "quickly ", "just ", "would you "])
    post = pick([""] * 9 + [" please", " for me", " now", " thanks", " pls", " real quick", " thx"])
    return pre + q + post


# ---------------------------------------------------------------- words

OPEN = ["open", "open", "open", "launch", "start", "run", "fire up", "bring up", "pull up", "open up", "start up",
        "get me", "give me", "load", "load up", "boot up", "spin up", "i need", "i want"]
OPEN_FR = ["ouvre", "ouvrir", "lance", "lancer", "démarre", "ouvre-moi"]
CLOSE = ["close", "close", "close", "quit", "exit", "kill", "shut", "close out", "get rid of", "exit out of", "end",
         "terminate", "close down", "quit out of"]
CLOSE_FR = ["ferme", "fermer", "quitte", "quitter"]
MIN = ["minimize {}", "minimise {}", "minimize {}", "hide {}", "shrink {}", "shrink {} down", "send {} to the taskbar",
       "tuck {} away", "put {} away"]
MIN_FR = ["réduis {}", "réduire {}", "minimise {}", "cache {}"]
MAX = ["maximize {}", "maximise {}", "maximize {}", "make {} full screen", "make {} fullscreen", "put {} in full screen",
       "{} full screen", "make {} bigger", "blow up {}", "enlarge {}", "make {} take the whole screen",
       "fill the screen with {}", "go full screen on {}"]
MAX_FR = ["agrandis {}", "maximise {}", "mets {} en plein écran"]
FOCUS = ["switch to {}", "go to {}", "focus {}", "focus on {}", "show {}", "show me {}", "bring {} to the front",
         "jump to {}", "take me to {}", "pull {} to the front", "bring back {}", "flip to {}", "go back to {}"]
FOCUS_FR = ["va sur {}", "passe à {}", "affiche {}", "montre-moi {}", "retourne sur {}"]
SNAP = ["snap", "snap", "put", "place", "move", "arrange", "tile", "line up", "position", "drag"]
SNAP_FR = ["mets", "place", "déplace", "range"]

SIDE = ["side by side", "side by side", "side-by-side", "next to each other", "beside each other", "snapped",
        "snapped together", "in split screen", "split screen", "split", "in halves", "in columns", "half and half",
        "50/50", "on either side", "one on each side", "and snap them", "and snap them side by side",
        "and put them side by side", "and put them next to each other", "and split the screen", "in two halves",
        "in snap layout", "snapped side by side"]
GRID = ["in a grid", "in quarters", "in the corners", "tiled", "in four corners", "as a grid", "and tile them",
        "in a 2x2 grid", "and arrange them in a grid", "in a grid layout"]
SIDE_FR = ["côte à côte", "l'un à côté de l'autre", "en écran partagé", "et mets-les côte à côte", "en deux colonnes"]
GRID_FR = ["en grille", "en mosaïque", "dans les coins"]
PLACES = {
    "left": ["on the left", "to the left", "left", "on the left side", "on the left half", "to the left half",
             "on the left of the screen", "to the left side"],
    "right": ["on the right", "to the right", "right", "on the right side", "on the right half", "to the right half",
              "on the right of the screen", "to the right side"],
    "top": ["on top", "at the top", "on the top half", "to the top"],
    "bottom": ["at the bottom", "on the bottom half", "to the bottom"],
}
PLACES_FR = {"left": ["à gauche", "sur la gauche", "sur la moitié gauche"], "right": ["à droite", "sur la droite"],
             "top": ["en haut"], "bottom": ["en bas"]}
PAIRS = [("left", "right")] * 5 + [("right", "left"), ("top", "bottom"), ("bottom", "top")]


def layout(n, fr=False):
    lay, words = ("grid", pick(GRID_FR if fr else GRID)) if n >= 3 and R.random() < 0.4 else ("side", pick(SIDE_FR if fr else SIDE))
    if R.random() < 0.15 and not words.startswith(("and ", "et ")):
        words = ", " + words   # "open a and b, side by side"
    return lay, words


def place(p, fr=False):
    return pick((PLACES_FR if fr else PLACES)[p])


def J(*steps):
    return " ; ".join(s for s in steps if s)


# ---------------------------------------------------------------- requests that open apps

def multi_open(fr):
    xs = apps(R.choice([2, 2, 2, 3, 3, 4]), fr)
    q = f"{pick(OPEN_FR if fr else OPEN)} {listing([s for s, _ in xs], fr)}"
    if not fr and R.random() < 0.1:
        q = f"{q} {pick(['open', 'up', 'at once', 'together', 'both'])}"
    return polite(q, fr), J(*[f"open {m}" for _, m in xs])


def open_layout(fr):
    n = R.choice([2, 2, 2, 3, 3, 4])
    xs = apps(n, fr)
    lay, words = layout(n, fr)
    if R.random() < 0.04 and " " in words:   # still typing
        w = words.split()
        words = " ".join(w[: R.randint(1, len(w) - 1)])
    v = pick(OPEN_FR if fr else OPEN)
    q = f"{v} {listing([s for s, _ in xs], fr)} {words}"
    if not fr and R.random() < 0.08:
        q = f"{v} {pick(['4 apps', '3 apps', 'these', 'these apps'])}: {listing([s for s, _ in xs])} {words}"
    return polite(q, fr), J(*[f"open {m}" for _, m in xs], f"snap {lay}")


def open_places(fr):
    v = pick(OPEN_FR if fr else OPEN)
    if R.random() < 0.4:
        (s, m), = apps(1, fr)
        p = pick(["left", "right", "left", "right", "top", "bottom"])
        return polite(f"{v} {s} {place(p, fr)}", fr), J(f"open {m}", f"snap {p}")
    (s1, m1), (s2, m2) = apps(2, fr)
    p1, p2 = pick(PAIRS)
    join = pick([" et ", ", "]) if fr else pick([" and ", " and ", ", ", " then ", " and "])
    second = f"{pick(OPEN_FR if fr else OPEN)} {s2}" if R.random() < 0.3 else s2
    q = f"{v} {s1} {place(p1, fr)}{join}{second} {place(p2, fr)}"
    return polite(q, fr), J(f"open {m1}", f"snap {p1}", f"open {m2}", f"snap {p2}")


# ---------------------------------------------------------------- windows already open

def snap(fr):
    k = R.random()
    sv = pick(SNAP_FR if fr else SNAP)
    if k < 0.3:
        n = R.choice([2, 2, 3, 4])
        xs = apps(n, fr)
        lay, words = layout(n, fr)
        if words.startswith("and ") or words.startswith("et "):
            lay, words = "side", pick(SIDE_FR[:3] if fr else SIDE[:12])
        if R.random() < 0.04 and " " in words:
            w = words.split()
            words = " ".join(w[: R.randint(1, len(w) - 1)])
        q = f"{sv} {listing([s for s, _ in xs], fr)} {words}"
        return polite(q, fr), f"snap {lay} " + ", ".join(m for _, m in xs)
    if k < 0.4 and not fr:
        xs = apps(R.choice([2, 2, 3]))
        q = f"{pick(['snap', 'tile', 'arrange', 'snap together'])} {listing([s for s, _ in xs])}"
        return polite(q), "snap side " + ", ".join(m for _, m in xs)
    if k < 0.65:
        (s, m), = apps(1, fr)
        p = pick(["left", "right", "left", "right", "top", "bottom"])
        if not fr and R.random() < 0.2:
            q = pick([f"make {s} take the {p} half", f"make {s} take the {p} half of the screen",
                      f"give {s} the {p} half", f"{s} to the {p}", f"dock {s} {p}"])
        else:
            q = f"{sv} {s} {place(p, fr)}"
        return polite(q, fr), f"snap {p} {m}"
    if k < 0.9:
        (s1, m1), (s2, m2) = apps(2, fr)
        p1, p2 = pick(PAIRS)
        join = pick([" et ", ", "]) if fr else pick([" and ", ", ", " and ", " with "])
        if R.random() < 0.35:
            q = f"{s1} {place(p1, fr)}{join}{s2} {place(p2, fr)}"
        else:
            q = f"{sv} {s1} {place(p1, fr)}{join}{s2} {place(p2, fr)}"
        return polite(q, fr), J(f"snap {p1} {m1}", f"snap {p2} {m2}")
    (s1, m1), (s2, m2) = apps(2)
    q = pick([f"split the screen between {s1} and {s2}", f"split my screen with {s1} and {s2}",
              f"share the screen between {s1} and {s2}", f"{s1} and {s2} split screen",
              f"half {s1} half {s2}", f"put {s1} beside {s2}", f"put {s1} next to {s2}"])
    return polite(q), f"snap side {m1}, {m2}"


def close_min_max(fr):
    k = R.random()
    if k < 0.4:
        xs = apps(R.choice([1, 1, 2, 2, 3]), fr)
        v = pick(CLOSE_FR if fr else CLOSE)
        q = f"{v} {listing([s for s, _ in xs], fr)}"
        if not fr and len(xs) == 1 and R.random() < 0.25:
            q = pick([f"close all {xs[0][0]} windows", f"close every {xs[0][0]} window", f"close all the {xs[0][0]} windows",
                      f"get rid of all the {xs[0][0]} windows"])
        return polite(q, fr), J(*[f"close {m}" for _, m in xs])
    if k < 0.7:
        xs = apps(R.choice([1, 1, 2, 2, 3]), fr)
        q = pick(MIN_FR if fr else MIN).format(listing([s for s, _ in xs], fr))
        return polite(q, fr), J(*[f"min {m}" for _, m in xs])
    (s, m), = apps(1, fr)
    return polite(pick(MAX_FR if fr else MAX).format(s), fr), f"max {m}"


def focus(fr):
    (s, m), = apps(1, fr)
    return polite(pick(FOCUS_FR if fr else FOCUS).format(s), fr), f"focus {m}"


def pronoun(fr):
    k = R.random()
    (s, m), = apps(1, fr)
    if fr:
        v = pick(OPEN_FR)
        if k < 0.5:
            p = pick(["left", "right"])
            return f"{v} {s} et {pick(['mets-le', 'place-le'])} {place(p, True)}", J(f"open {m}", f"snap {p}")
        return f"{v} {s} et {pick(['agrandis-le', 'mets-le en plein écran'])}", J(f"open {m}", f"max {m}")
    if k < 0.25:
        it = pick(["maximize it", "make it full screen", "make it fullscreen", "put it in full screen", "make it big",
                   "maximise it", "go full screen"])
        return polite(f"{pick(OPEN)} {s} and {it}"), J(f"open {m}", f"max {m}")
    if k < 0.5:
        p = pick(["left", "right", "left", "right", "top", "bottom"])
        it = pick(["put it", "snap it", "move it", "place it", "dock it"])
        return polite(f"{pick(OPEN)} {s} {pick(['and', 'then', 'and then'])} {it} {place(p)}"), J(f"open {m}", f"snap {p}")
    if k < 0.7:
        which = pick(["max", "min"])
        it = pick(["maximize it", "make it full screen"] if which == "max" else ["minimize it", "hide it"])
        return polite(f"{pick(FOCUS).format(s)} and {it}"), J(f"focus {m}", f"{which} {m}")
    if k < 0.8:
        return polite(f"{pick(OPEN)} {s} {pick(['then', 'and'])} {pick(['hide it', 'minimize it'])}"), J(f"open {m}", f"min {m}")
    xs = apps(R.choice([2, 2, 3]))
    lay, words = layout(len(xs))
    words = words.removeprefix("and ")
    them = pick(["put them", "snap them", "arrange them", "place them", "lay them out"])
    q = f"{pick(OPEN)} {listing([s for s, _ in xs])} and {them} {words}"
    return polite(q), J(*[f"open {mm}" for _, mm in xs], f"snap {lay}")


def mixed(fr):
    """Two or three steps, each with its own verb."""
    n = R.choice([2, 2, 2, 3])
    xs = apps(n, fr)
    parts, steps = [], []
    for s, m in xs:
        op = pick(["open", "close", "min", "focus", "max", "open", "close"])
        if fr:
            words = {"open": pick(OPEN_FR) + " {}", "close": pick(CLOSE_FR) + " {}", "min": pick(MIN_FR),
                     "focus": pick(FOCUS_FR), "max": pick(MAX_FR)}[op]
        else:
            words = {"open": pick(OPEN) + " {}", "close": pick(CLOSE) + " {}", "min": pick(MIN[:5]),
                     "focus": pick(FOCUS[:6]), "max": pick(MAX[:5])}[op]
        parts.append(words.format(s))
        steps.append(f"{op} {m}")
    joins = [" et ", " puis ", ", puis ", " et puis "] if fr else [" and ", " then ", ", then ", " and then ", ", "]
    q = parts[0]
    for p in parts[1:]:
        q += pick(joins) + p
    return polite(q, fr), J(*steps)


# ---------------------------------------------------------------- the PC itself

DESKTOP = ["show desktop", "show the desktop", "show my desktop", "show me the desktop", "go to desktop",
           "go to the desktop", "minimize everything", "minimize all windows", "minimize all", "minimise everything",
           "hide all windows", "hide all my windows", "hide everything", "clear the screen", "clear my screen",
           "peek at the desktop", "take me to the desktop", "get everything out of the way", "minimize all my windows"]
DESKTOP_FR = ["affiche le bureau", "montre le bureau", "réduis tout", "réduis toutes les fenêtres", "va sur le bureau"]
SHOT = ["take a screenshot", "take screenshot", "screenshot the screen", "screenshot part of the screen",
        "snip a region of my screen", "capture the screen", "capture my screen", "grab a screenshot", "snip the screen",
        "take a snip", "screen capture", "make a screenshot", "print screen", "take a screen shot"]
SHOT_FR = ["fais une capture d'écran", "capture d'écran", "prends une capture d'écran", "capture l'écran"]
LOCK = ["lock my pc", "lock the pc", "lock the computer", "lock my computer", "lock my laptop", "lock the screen",
        "lock this pc", "lock windows", "lock up", "lock my screen", "lock the laptop", "lock it"]
LOCK_FR = ["verrouille le pc", "verrouille l'ordinateur", "verrouille l'écran", "verrouille mon pc"]
SLEEP = ["put my computer to sleep", "put the pc to sleep", "put my pc to sleep", "put the laptop to sleep",
         "go to sleep", "sleep the computer", "sleep mode", "make the computer sleep", "send the pc to sleep"]
SLEEP_FR = ["mets le pc en veille", "mise en veille", "mets l'ordinateur en veille"]
CLOSEALL = ["close everything", "close all windows", "close all apps", "quit all my apps", "close all my windows",
            "close every window", "quit everything", "close all programs", "close all open apps", "shut everything",
            "close all the windows", "kill all apps"]
EXCEPT = ["except", "but", "apart from", "other than", "besides", "but not", "except for", "save"]
CLOSEALL_FR = ["ferme tout", "ferme toutes les fenêtres", "ferme toutes les applis", "quitte tout"]


def system(fr):
    k = R.random()
    if k < 0.25:
        if not fr and R.random() < 0.25:
            (s, m), = apps(1)
            q = f"{pick(['minimize everything', 'minimise all windows', 'hide all windows', 'minimize all'])} " \
                f"{pick(['except', 'but', 'apart from', 'other than'])} {s}"
            return polite(q), J("desktop", f"focus {m}")
        return polite(pick(DESKTOP_FR if fr else DESKTOP), fr), "desktop"
    if k < 0.45:
        if not fr and R.random() < 0.4:
            q = f"{pick(['screenshot', 'snip', 'capture', 'grab', 'clip', 'take a screenshot of', 'snip out'])} "                 f"{pick(['part of', 'a part of', 'a region of', 'an area of', 'a section of', 'a bit of', 'a piece of', 'some of', 'a portion of'])} "                 f"{pick(['the screen', 'my screen', 'the display', 'what is on my screen'])}"
            return polite(q), "shot"
        return polite(pick(SHOT_FR if fr else SHOT), fr), "shot"
    if k < 0.65:
        return polite(pick(LOCK_FR if fr else LOCK), fr), "lock"
    if k < 0.8:
        return polite(pick(SLEEP_FR if fr else SLEEP), fr), "sleep"
    # with something else first
    q1, p1 = pick([quick, media])(fr)
    last, step = pick([(LOCK_FR if fr else LOCK, "lock"), (SLEEP_FR if fr else SLEEP, "sleep"),
                       (DESKTOP_FR if fr else DESKTOP, "desktop")])
    return f"{q1}{pick([' et ', ' puis '] if fr else [' and ', ' then ', ' and then '])}{pick(last)}", J(p1, step)


def closeall(fr):
    q = pick(CLOSEALL_FR if fr else CLOSEALL)
    if R.random() < 0.55:
        xs = apps(R.choice([1, 1, 2]), fr)
        q = f"{q} {pick(['sauf', 'à part']) if fr else pick(EXCEPT)} {listing([s for s, _ in xs], fr)}"
        return polite(q, fr), "closeall except " + ", ".join(m for _, m in xs)
    return polite(q, fr), "closeall"


# ---------------------------------------------------------------- music and quick settings

MEDIA = {
    "play": ["pause the music", "pause music", "play music", "resume music", "resume my music", "pause the song",
             "pause whatever is playing", "pause playback", "resume playback", "play the music", "unpause the music",
             "pause my music", "keep playing", "continue the music", "hit play", "hit pause", "pause spotify"[:0] or
             "pause the track", "play some music", "start the music"],
    "next": ["next song", "next track", "skip song", "skip this song", "skip the song", "skip to the next track",
             "skip to the next song", "play the next song", "next one", "skip this track", "go to the next song"],
    "prev": ["previous song", "previous track", "go back a song", "go back to the previous song", "last song",
             "play the previous track", "play the last song again", "back one song", "rewind to the last track"],
    "stop": ["stop the music", "stop music", "stop playback", "stop playing", "stop the song", "stop all music"],
}
MEDIA_FR = {"play": ["mets la musique en pause", "pause la musique", "relance la musique", "reprends la musique"],
            "next": ["chanson suivante", "musique suivante", "piste suivante", "passe à la chanson suivante"],
            "prev": ["chanson précédente", "piste précédente"], "stop": ["arrête la musique", "stop la musique"]}


def media(fr):
    what = pick(["play", "play", "next", "next", "prev", "stop"])
    if not fr and R.random() < 0.25:
        (s, m), = apps(1)
        tail = pick(["play some music", "play music", "play", "start playing", "hit play", "resume the music"])
        return polite(f"{pick(OPEN)} {s} and {tail}"), J(f"open {m}", "media play")
    return polite(pick((MEDIA_FR if fr else MEDIA)[what]), fr), f"media {what}"


RADIO = {"wifi": ["wifi", "wi-fi", "the wifi", "wireless", "the wi-fi", "wlan", "wifi"], "bluetooth": ["bluetooth", "the bluetooth", "bt", "bluetooth"]}
QUICK = {
    ("dark", "on"): ["turn on dark mode", "dark mode on", "enable dark mode", "switch to dark mode", "go dark",
                     "make windows dark", "use the dark theme", "enable the dark theme", "turn dark mode on",
                     "activate dark mode", "set dark mode", "turn off light mode", "light mode off", "dark theme please",
                     "switch the theme to dark", "make everything dark"],
    ("dark", "off"): ["turn on light mode", "switch to light mode", "light mode on", "turn off dark mode", "disable dark mode",
                      "dark mode off", "make windows light", "use the light theme", "go back to light mode",
                      "switch the theme to light", "enable light mode", "turn dark mode off"],
    ("dark", "toggle"): ["toggle dark mode", "switch the theme", "flip the theme", "toggle the theme", "toggle light mode",
                         "change the theme"],
    ("mute", "on"): ["mute the sound", "mute the audio", "mute my pc", "mute the computer", "silence my pc",
                     "silence the sound", "turn the sound off", "turn off the sound", "sound off", "kill the sound",
                     "mute everything", "mute the volume", "mute", "mute my laptop", "mute audio"],
    ("mute", "off"): ["unmute", "unmute the sound", "unmute my pc", "turn the sound back on", "turn the sound on", "sound on",
                      "restore the sound", "unmute my laptop", "unmute audio", "bring the sound back"],
    ("volume", "up"): ["turn the volume up", "volume up", "make it louder", "increase the volume", "raise the volume",
                       "turn it up", "pump up the volume", "turn up the sound", "make the sound louder", "louder please",
                       "a bit louder", "turn up the volume"],
    ("volume", "down"): ["turn the volume down", "volume down", "make it quieter", "lower the volume", "decrease the volume",
                         "turn it down", "quieter please", "reduce the volume", "lower the sound", "lower the volume a bit",
                         "a bit quieter", "turn down the volume", "it's too loud"],
    ("brightness", "up"): ["make the screen brighter", "increase brightness", "turn up the brightness", "brighter screen",
                           "raise the brightness", "brightness up", "increase the screen brightness", "brighten the screen"],
    ("brightness", "down"): ["dim the screen", "make the screen darker", "lower the brightness", "decrease brightness",
                             "turn down the brightness", "brightness down", "dim my display", "reduce the brightness",
                             "the screen is too bright"],
}
RADIO_ON = ["turn on {}", "turn {} on", "enable {}", "{} on", "switch on {}", "activate {}", "switch {} on", "turn on {} please"]
RADIO_OFF = ["turn off {}", "turn {} off", "disable {}", "{} off", "switch off {}", "kill the {}", "deactivate {}",
             "switch {} off", "turn of {}", "turn {} of"]
RADIO_TOGGLE = ["toggle {}", "flip {}", "toggle {} on or off"]
LEVEL = {"volume": ["set the volume to {}", "set volume to {}", "volume {}", "volume to {}", "volume at {}", "volume {}%",
                    "volume to {} percent", "put the volume at {}", "turn the volume to {}", "change the volume to {}%",
                    "set sound to {}", "vol {}"],
         "brightness": ["set brightness to {}", "brightness {}", "brightness to {}%", "screen brightness {}",
                        "set the screen brightness to {} percent", "make the brightness {}", "brightness at {}",
                        "set the brightness to {}"]}
QUICK_FR = {
    ("dark", "on"): ["active le mode sombre", "passe en mode sombre", "mode sombre"],
    ("dark", "off"): ["passe en mode clair", "désactive le mode sombre", "mode clair", "active le mode clair"],
    ("wifi", "on"): ["active le wifi", "allume le wifi"], ("wifi", "off"): ["désactive le wifi", "coupe le wifi", "éteins le wifi"],
    ("bluetooth", "on"): ["active le bluetooth", "allume le bluetooth"],
    ("bluetooth", "off"): ["désactive le bluetooth", "coupe le bluetooth", "éteins le bluetooth"],
    ("mute", "on"): ["coupe le son", "mets en sourdine", "désactive le son"], ("mute", "off"): ["remets le son", "réactive le son"],
    ("volume", "up"): ["monte le son", "augmente le volume", "plus fort"],
    ("volume", "down"): ["baisse le son", "baisse le volume", "moins fort"],
    ("brightness", "up"): ["augmente la luminosité"], ("brightness", "down"): ["baisse la luminosité"],
}
LEVEL_FR = {"volume": ["mets le volume à {}", "volume à {}", "règle le volume à {}"],
            "brightness": ["mets la luminosité à {}", "luminosité à {}"]}


def quick(fr):
    k = R.random()
    if fr:
        if k < 0.25:
            what = pick(["volume", "brightness"])
            n = R.choice(range(0, 101, R.choice([5, 10, 1])))
            return pick(LEVEL_FR[what]).format(n), f"set {what} {n}"
        (what, value), qs = pick(list(QUICK_FR.items()))
        return pick(qs), f"set {what} {value}"
    if k < 0.3:
        radio = pick(["wifi", "bluetooth"])
        value, frames = pick([("on", RADIO_ON), ("off", RADIO_OFF), ("toggle", RADIO_TOGGLE), ("on", RADIO_ON), ("off", RADIO_OFF)])
        return pick(frames).format(pick(RADIO[radio])), f"set {radio} {value}"
    if k < 0.5:
        what = pick(["volume", "volume", "brightness"])
        n = R.choice(range(0, 101, R.choice([5, 10, 10, 1])))
        return pick(LEVEL[what]).format(n), f"set {what} {n}"
    (what, value), qs = pick(list(QUICK.items()))
    return pick(qs), f"set {what} {value}"


def quick_combo(fr):
    """A quick setting with something else: another setting, an app, music."""
    q1, p1 = quick(fr)
    k = R.random()
    if k < 0.4:
        q2, p2 = quick(fr)
        if p2.split()[1] == p1.split()[1]:
            return polite(q1, fr), p1
    elif k < 0.75:
        (s, m), = apps(1, fr)
        q2, p2 = f"{pick(OPEN_FR if fr else OPEN)} {s}", f"open {m}"
    else:
        q2, p2 = media(fr)
        if p2.startswith("open"):
            return polite(q1, fr), p1
    if R.random() < 0.5:
        q1, p1, q2, p2 = q2, p2, q1, p1
    if not fr and p1.startswith("set wifi") and p2.startswith("set bluetooth") and R.random() < 0.3:
        v1, v2 = p1.split()[2], p2.split()[2]
        if v1 == v2 and v1 in ("on", "off"):
            return polite(f"turn {v1} wifi and bluetooth"), J(p1, p2)
    join = pick([" et ", " puis "]) if fr else pick([" and ", " then ", ", ", " and then ", " and also "])
    return polite(q1 + join + q2, fr), J(p1, p2)


# ---------------------------------------------------------------- settings pages, folders, websites

SETTING_SAYS = [s.lower() for g in CATALOG["settings"] for s in g]
SETTING_TASKS = [
    ("change my wallpaper", "wallpaper"), ("change the wallpaper", "wallpaper"), ("change my background", "background"),
    ("change the desktop background", "desktop background"), ("uninstall an app", "uninstall"),
    ("i want to uninstall an app", "uninstall"), ("remove a program", "uninstall"), ("uninstall a program", "uninstall"),
    ("check for updates", "windows update"), ("check for windows updates", "windows update"), ("update windows", "windows update"),
    ("install updates", "windows update"), ("add a printer", "printer"), ("set up a printer", "printer"),
    ("pair a bluetooth device", "bluetooth"), ("connect my headphones", "bluetooth"), ("pair my earbuds", "bluetooth"),
    ("change the resolution", "display"), ("change screen resolution", "display"), ("change the screen scale", "display"),
    ("change my accent color", "accent color"), ("turn on night light", "night light"), ("enable night light", "night light"),
    ("turn off night light", "night light"), ("set up a vpn", "vpn"), ("add a vpn", "vpn"), ("change the time zone", "time"),
    ("change the date and time", "date"), ("change the language", "language"), ("change the display language", "language"),
    ("free up disk space", "storage"), ("clean up my disk", "storage"), ("check my battery", "battery"),
    ("battery saver", "battery"), ("manage startup apps", "startup"), ("stop apps from starting up", "startup"),
    ("change default browser", "default apps"), ("change my default apps", "default apps"), ("turn on hotspot", "hotspot"),
    ("share my internet", "hotspot"), ("change mouse speed", "mouse"), ("change the mouse sensitivity", "mouse"),
    ("turn off notifications", "notifications"), ("stop notifications", "notifications"), ("check my pc specs", "system info"),
    ("how much ram do i have"[:0] or "show my pc info", "system info"), ("change my lock screen", "lock screen"),
    ("customize the taskbar", "taskbar"), ("change the taskbar", "taskbar"), ("change my password", "account"),
    ("manage my account", "account"), ("change the theme colors", "colors"), ("turn on game mode", "game mode"),
    ("change microphone settings", "microphone"), ("test my mic", "mic"), ("change touchpad gestures", "touchpad"),
    ("reset this pc", "recovery"), ("activate windows", "activation"), ("location settings", "location"),
    ("clipboard history", "clipboard"), ("change sound output", "sound"), ("change my speakers", "speakers"),
    ("connect to wifi", "wifi"), ("join a wifi network", "wifi"), ("network settings", "network"),
]
SETTINGS_FRAMES = ["open {} settings", "{} settings", "go to {} settings", "take me to the {} settings",
                   "show me {} settings", "open the {} settings page", "open settings for {}", "settings for {}",
                   "open the {} settings", "open {} in settings", "bring up {} settings", "go to settings > {}"]


def settings(fr):
    if fr:
        s = pick(SETTING_SAYS)
        return polite(pick(["ouvre les paramètres {}", "paramètres {}", "va dans les paramètres {}"]).format(s), True), f"settings {s}"
    if R.random() < 0.45:
        q, s = pick(SETTING_TASKS)
        return polite(q), f"settings {s}"
    s = pick(SETTING_SAYS)
    return polite(pick(SETTINGS_FRAMES).format(s)), f"settings {s}"


def settings_combo(fr):
    q1, p1 = settings(False)
    k = R.random()
    if k < 0.35:
        q2, p2 = settings(False)
        q2 = q2.removeprefix("open ")
    elif k < 0.7:
        q2, p2 = quick(False)
    else:
        (s, m), = apps(1)
        q2, p2 = f"{pick(OPEN)} {s}", f"open {m}"
    if p1 == p2:
        return q1, p1
    return q1 + pick([" and ", " then ", ", "]) + q2, J(p1, p2)


FOLDER_SAY = {
    "downloads": ["downloads", "download folder", "downloads folder"], "documents": ["documents", "docs", "documents folder"],
    "desktop": ["desktop folder"], "pictures": ["pictures", "pictures folder", "images folder"],
    "music": ["music folder"], "videos": ["videos", "videos folder"], "home": ["home folder", "user folder"],
    "recycle bin": ["recycle bin", "trash", "bin"], "this pc": ["this pc", "my computer"],
    "onedrive": ["onedrive folder"], "appdata": ["appdata", "appdata folder"], "temp": ["temp folder"],
    "program files": ["program files"],
}
FOLDER_FRAMES = ["open my {}", "open the {}", "open {}", "show me my {}", "go to my {}", "open file explorer in {}",
                 "open explorer at {}", "browse my {}", "open up my {}", "take me to my {}", "show my {}"]
FOLDER_FR = [("ouvre mes téléchargements", "downloads"), ("ouvre le dossier téléchargements", "downloads"),
             ("ouvre mes documents", "documents"), ("ouvre la corbeille", "recycle bin"), ("ouvre mes images", "pictures"),
             ("ouvre ce pc", "this pc"), ("ouvre le dossier vidéos", "videos")]


def folder(fr):
    if fr:
        q, f = pick(FOLDER_FR)
        return polite(q, True), f"folder {f}"
    f = pick(list(FOLDER_SAY))
    q = pick(FOLDER_FRAMES).format(pick(FOLDER_SAY[f]))
    if R.random() < 0.3:
        k = R.random()
        if k < 0.5:
            f2 = pick([x for x in FOLDER_SAY if x != f])
            return polite(f"{q} and {pick(FOLDER_SAY[f2])}"), J(f"folder {f}", f"folder {f2}")
        (s, m), = apps(1)
        return polite(f"{q} and {s}"), J(f"folder {f}", f"open {m}")
    return polite(q), f"folder {f}"


SITES = """youtube.com github.com reddit.com wikipedia.org gmail.com google.com amazon.com netflix.com twitch.tv x.com
twitter.com linkedin.com stackoverflow.com chatgpt.com claude.ai news.ycombinator.com lemonde.fr leboncoin.fr
bbc.co.uk docs.google.com outlook.com maps.google.com figma.com notion.so discord.com spotify.com apple.com
microsoft.com duckduckgo.com bing.com imdb.com ebay.com paypal.com instagram.com facebook.com tiktok.com
pinterest.com medium.com dev.to vercel.com archive.org openai.com anthropic.com nytimes.com theverge.com""".split()
_TLD = ["com", "org", "net", "io", "dev", "fr", "co.uk", "app", "ai"]


def site():
    if R.random() < 0.7:
        return pick(SITES)
    return pick(_A + _B + ["shop", "blog", "news", "cafe", "studio", "travel"]) + pick(_A + ["", "", "hq", "now"]) + "." + pick(_TLD)


def url(fr):
    d = site()
    shown = pick(["", "", "", "www.", "https://"]) + d
    if fr:
        return polite(pick(["va sur {}", "ouvre {}"]).format(shown), True), f"url {d}"
    q = pick(["go to {}", "open {}", "visit {}", "take me to {}", "open {} in the browser", "pull up {}", "browse to {}",
              "navigate to {}", "open the website {}", "load {}", "open {} in chrome", "{}"]).format(shown)
    if R.random() < 0.25:
        (s, m), = apps(1)
        if R.random() < 0.5:
            return polite(f"{q} and {pick(OPEN)} {s}"), J(f"url {d}", f"open {m}")
        return polite(f"{pick(OPEN)} {s} and {q}"), J(f"open {m}", f"url {d}")
    return polite(q), f"url {d}"


# ---------------------------------------------------------------- searches, not requests

NOUNS = ["download", "update", "not working", "extensions", "shortcuts", "keyboard shortcuts", "premium", "price",
         "alternative", "login", "crashing", "dark theme", "plugins", "tutorial", "tips", "free", "for mac", "review",
         "logo", "won't open", "keeps crashing", "high cpu usage", "uninstall", "settings", "account", "help",
         "release notes", "beta", "version", "install", "sign in", "error", "black screen", "slow", "tricks", "features"]
PHRASES = """open source|open source software|open world games|open house|open university|close reading|
close encounters|start menu|start menu not opening|startup ideas|startup companies|run commands|run club|kill bill|
kill switch|lock screen wallpaper|lock picking|sleep timer|sleep timer for pc|sleep music|sleep sounds|next week|
next level|skip the line|play store|play games|pause menu|stop sign|show hidden files|show me cat videos|snapchat|
split screen not working|move files to onedrive|put a picture in word|quit smoking|exit poll|minimize risk|
maximize profit|focus music|focus mode|switch games|nintendo switch|mute swan|dark mode wallpaper|dark mode wallpaper 4k|
volume of a sphere|brightness meaning|wifi password|bluetooth headphones not connecting|desktop wallpaper|
screenshot folder|close all tabs in chrome|show desktop icons|tile flooring|snap fitness|put options|move to canada|
open office hours|close friends|start a business|run windows on mac|desktop background images|volume mixer|
brightness not working|wifi not working|bluetooth not showing up|dark souls|light mode vs dark mode|
why is my pc slow|what time is it|weather tomorrow|weather today|news|translate hello to french|
best apps for windows 11|chrome vs firefox|budget report 2024.xlsx|invoice march.pdf|tax return|resume.docx|
cat pictures|pizza near me|flights to london|2 + 2 * 3|15% of 80|convert 10 usd to eur|run as administrator|
notepad and weather|lock and key|game of thrones|the last of us|open ai|openai|play doh|show me the money|
window cleaning|windows 11 pro key|close the gap|focus group|kill time|switch statement in c|maximize window shortcut|
how to minimize|how to lock screen|define snapped|split ends|desktop fan|quit job|end of the world|terminal velocity|
explorer of the seas|paint colors|word of the day|excel formulas|teams background|steam deck|photos of paris|
calendar 2025|clock tower|maps of france|store hours|music theory|code of conduct|mail merge|calculator app for mac""".replace("\n", "").split("|")
DANGER = ["shut down my computer", "shut down the pc", "restart the pc", "restart my computer", "reboot", "turn off the computer",
          "delete all my files", "format the c drive", "empty the recycle bin", "log out", "sign out", "factory reset",
          "delete system32", "uninstall windows", "wipe my hard drive", "power off", "shut down"]
NEG_FR = ["météo demain", "comment faire une capture d'écran", "comment ouvrir le gestionnaire des tâches", "recette crêpes",
          "actualités", "horaires de la poste", "traduire bonjour en anglais", "fond d'écran mode sombre", "mot de passe wifi",
          "bluetooth ne marche pas", "éteindre l'ordinateur", "redémarre le pc", "météo toulouse", "film ce soir"]
CITIES = ["paris", "london", "toulouse", "new york", "tokyo", "berlin", "lyon", "madrid", "montreal", "sydney"]
FAMILIES = None  # set below


def negative(fr):
    if fr:
        return pick(NEG_FR), "none"
    k = R.random()
    if k < 0.18:
        s, _ = a_name()
        return (s if R.random() < 0.7 else f"{s} {pick(NOUNS)}"), "none"
    if k < 0.3:
        (s1, _), (s2, _) = apps(2)
        return pick([f"{s1} and {s2}", f"{s1} vs {s2}", f"{s1} or {s2}", f"{s1} {s2}"]), "none"
    if k < 0.55:
        # A request asked about, not asked for.
        fam = pick([f for f, w in FAMILIES if f not in (negative,)])
        q, _ = fam(False)
        q = re.sub(r"^(please |can you |could you |hey |ok |i want to |i'd like to |go ahead and |quickly |just |would you )", "", q)
        pre = pick(["how to ", "how do i ", "how can i ", "how do you ", "why can't i ", "is it possible to ", "can i ",
                    "what happens if i ", "tutorial ", "shortcut to ", "keyboard shortcut to ", "what is the shortcut to ",
                    "why won't windows let me ", "how to quickly "])
        return pre + q + pick(["", "", "", "?", " on windows", " in windows 11", " on windows 11"]), "none"
    if k < 0.72:
        return pick(PHRASES), "none"
    if k < 0.8:
        return numbers(), "none"
    if k < 0.9:
        return pick(DANGER), "none"
    return pick([f"weather in {pick(CITIES)}", f"time in {pick(CITIES)}", f"{pick(CITIES)} news",
                 f"hotels in {pick(CITIES)}", f"{a_name()[0]} news"]), "none"


def numbers():
    """Sums, conversions and dates: the calculator's, never a plan."""
    n = lambda: str(R.choice([R.randint(0, 20), R.randint(0, 1000), round(R.uniform(0, 100), R.choice([1, 2]))]))
    op = lambda: pick([" + ", " - ", " * ", " / ", "+", "-", "*", "/", " x ", "^", " ^ ", " plus ", " times ", " minus "])
    k = R.random()
    if k < 0.5:
        e = n()
        for _ in range(R.choice([1, 1, 2, 3])):
            e += op() + n()
        return pick(["{}", "{}", "({})", "{} =", "what is {}", "calculate {}", "= {}"]).format(e)
    if k < 0.7:
        return pick([f"sqrt({n()})", f"{n()}% of {n()}", f"{n()}%", f"{n()}^2", f"sin({n()})", f"log {n()}", f"{n()}!"])
    if k < 0.85:
        return f"{n()} {pick(['km', 'miles', 'kg', 'lbs', 'usd', 'eur', 'c', 'f', 'cm', 'inches', 'gb', 'mb'])} "                f"{pick(['to', 'in', 'into'])} {pick(['miles', 'km', 'lbs', 'kg', 'eur', 'usd', 'f', 'c', 'inches', 'cm', 'mb', 'gb'])}"
    return pick([f"{R.randint(1, 28)}/{R.randint(1, 12)}/{R.randint(1990, 2030)}", f"{R.randint(1990, 2030)}-{R.randint(1, 12):02}",
                 f"+33 6 {R.randint(10, 99)} {R.randint(10, 99)} {R.randint(10, 99)} {R.randint(10, 99)}", str(R.randint(0, 99999))])


def slip(q, plan):
    """A typo in the request's own words ("opne", "lauch", "minimze"); names keep theirs."""
    words = q.split(" ")
    keep = set(re.findall(r"[^\W\d_]+", plan.lower()))
    idx = [i for i, w in enumerate(words) if len(w) >= 4 and w.isalpha() and w.lower() not in keep]
    if not idx:
        return q
    i = R.choice(idx[:2] if R.random() < 0.6 else idx)   # mostly the verb, at the start
    words[i] = typo(words[i])
    return " ".join(words)


FAMILIES = [(multi_open, 9), (open_layout, 10), (open_places, 6), (snap, 10), (close_min_max, 9), (focus, 3),
            (pronoun, 6), (mixed, 7), (system, 7), (closeall, 3), (media, 4), (quick, 6), (quick_combo, 5),
            (settings, 4), (settings_combo, 2), (folder, 3), (url, 3), (negative, 34)]
FRENCH = {multi_open, open_layout, open_places, snap, close_min_max, focus, pronoun, mixed, system, closeall, media,
          quick, quick_combo, settings, folder, url, negative}


def cut_last_name(q, plan):
    """Still typing: the last name is only begun ("open notepad and wea")."""
    last = plan.split(" ; ")[-1]
    verb, _, m = last.partition(" ")
    if verb not in ("open", "close", "min", "focus") or len(m) < 4 or not q.endswith(m) or " " in m:
        return q, plan
    k = R.randint(2, len(m) - 1)
    return q[: len(q) - len(m)] + m[:k], plan[: len(plan) - len(m)] + m[:k]


def one():
    fam = R.choices([f for f, _ in FAMILIES], [w for _, w in FAMILIES])[0]
    fr = fam in FRENCH and R.random() < 0.12
    q, p = fam(fr)
    if R.random() < 0.08:
        q, p = cut_last_name(q, p)
    if R.random() < 0.12:
        q = slip(q, p)
    q = re.sub(r"\s+,", ",", q)
    if R.random() < 0.08:
        q = q[0].upper() + q[1:]
    return re.sub(r"\s+", " ", q).strip(), p


POLITE = re.compile(r"^((please|can you|could you|would you|hey|ok|i want to|i'd like to|go ahead and|quickly|just|"
                    r"peux-tu|stp|s'il te plaît) )+|( (please|for me|now|thanks|pls|real quick|thx|stp|s'il te plaît|merci))+$")


def key(q):
    """The request without its politeness, to keep the test's requests out in any wording."""
    return POLITE.sub("", re.sub(r"[^\w']+", " ", q.lower()).strip()).strip()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--n", type=int, default=30000)
    ap.add_argument("--valid", type=int, default=1000)
    ap.add_argument("--seed", type=int, default=7)
    args = ap.parse_args()
    R.seed(args.seed)

    test = {key(json.loads(l)["q"]) for l in (HERE / "test.jsonl").read_text(encoding="utf-8").splitlines() if l.strip()}
    seen, rows = set(), []
    tries = 0
    while len(rows) < args.n + args.valid and tries < (args.n + args.valid) * 20:
        tries += 1
        q, p = one()
        k = key(q)
        if k in seen or k in test or not q:
            continue
        parse(p)   # every plan must be well formed
        seen.add(k)
        rows.append({"q": q, "plan": p})
    R.shuffle(rows)
    out = HERE / "data"
    out.mkdir(exist_ok=True)
    for name, part in [("valid", rows[: args.valid]), ("train", rows[args.valid:])]:
        with open(out / f"{name}.jsonl", "w", encoding="utf-8") as f:
            for r in part:
                f.write(json.dumps(r, ensure_ascii=False) + "\n")
    none = sum(r["plan"] == "none" for r in rows)
    print(f"{len(rows) - args.valid} train, {args.valid} valid, {100 * none / len(rows):.0f}% not requests")


if __name__ == "__main__":
    main()
