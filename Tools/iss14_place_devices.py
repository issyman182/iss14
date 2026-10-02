#!/usr/bin/env python3
"""iss14: places economy / casino devices on station maps.

Text-based: reads a map yaml, builds a tile + entity occupancy index of the main grid, finds free floor tiles next
to anchor entities (bar vendor, records console, cargo consoles ...) and appends new entities with fresh uids.
Never touches existing entities. Run with --dry to only print the plan.
"""
import base64, math, re, struct, sys
from collections import defaultdict

CHUNK = 16

# Prototype name prefixes that do NOT block a tile: under-floor / wall-mounted / floor-flush infrastructure.
PASSABLE = ("CableApcExtension", "CableMV", "CableHV", "CableTerminal", "GasPipe", "GasPort", "GasVent", "GasPassiveVent",
            "GasScrubber", "GasDualPort", "Catwalk", "Carpet", "Poweredlight", "PoweredSmallLight", "PoweredLight",
            "PoweredlightEmpty", "AlwaysPoweredLight", "LightTube", "APC", "Apc", "SurveillanceCamera", "Poster",
            "Signs", "Sign", "AirAlarm", "FireAlarm", "ExtinguisherCabinet", "Intercom", "WarpPoint", "EmergencyLight",
            "Sprinkler", "AtmosDeviceFan", "Wire", "DirtTile", "FloorDrain", "Mirror", "Painting", "Noticeboard",
            "Clock", "TelecomServer", "StationMap", "NavMapBeacon", "DefaultStationBeacon", "DefibrillatorCabinet",
            "DisposalPipe", "DisposalTrunk", "DisposalJunction", "DisposalBend", "DisposalTagger", "DisposalRouter",
            "DisposalSignalRouter", "DisposalYJunction", "Railing", "ButtonFrame", "SignalButton", "LightSwitch",
            "RandomPosters", "RandomPainting", "Puddle", "CrayonDecal", "AirSensor", "Doorbell", "AtmosFix", "Screen",
            "WallmountTelescreen", "Telescreen", "SignalSwitch", "TwoWayLever")

WALLISH = ("Wall", "Window", "Grille", "Shutter", "Airlock", "Door", "Firelock", "Blast")
DOORISH = ("Airlock", "Door", "Firelock", "Blast")


def tile_of(pos):
    x, y = pos
    return (math.floor(x), math.floor(y))


class MapFile:
    def __init__(self, path):
        self.path = path
        self.text = open(path, encoding="utf-8").read()
        self.lines = self.text.split("\n")
        self.grid = None
        self.entities = []          # (proto, uid, (x,y), parent, line_index_of_uid, rot)
        self.floor = set()          # tiles with a non-space tile on the main grid
        self.max_uid = 0
        self._parse()

    # ---- parsing -------------------------------------------------------------------------------------------
    def _parse(self):
        m = re.search(r"^grids:\n- (\d+)", self.text, re.M)
        self.grid = m.group(1)
        self._parse_tiles()
        proto = None
        uid = None
        uid_line = None
        pos = None
        parent = None
        rot = None
        in_transform = False
        for i, line in enumerate(self.lines):
            if line.startswith("- proto: "):
                proto = line[9:].strip()
                continue
            if line.startswith("  - uid: "):
                if uid is not None:
                    self._add(proto, uid, pos, parent, uid_line, rot)
                uid = int(line[9:])
                self.max_uid = max(self.max_uid, uid)
                uid_line = i
                pos = parent = rot = None
                in_transform = False
                continue
            if uid is None:
                continue
            if line.startswith("    - type: "):
                in_transform = line.strip() == "- type: Transform"
                continue
            if in_transform:
                s = line.strip()
                if s.startswith("pos: "):
                    x, y = s[5:].split(",")
                    pos = (float(x), float(y))
                elif s.startswith("parent: "):
                    parent = s[8:].strip()
                elif s.startswith("rot: "):
                    rot = s[5:].strip()
        if uid is not None:
            self._add(proto, uid, pos, parent, uid_line, rot)

    def _add(self, proto, uid, pos, parent, uid_line, rot):
        self.entities.append((proto, uid, pos, parent, uid_line, rot))

    def _parse_tiles(self):
        # Only the main grid's chunk block.
        start = self.text.index(f"  - uid: {self.grid}\n")
        block_end = self.text.find("\n  - uid: ", start + 10)
        block = self.text[start:block_end if block_end != -1 else None]
        for m in re.finditer(r"ind: (-?\d+),(-?\d+)\n\s+tiles: ([A-Za-z0-9+/=]+)", block):
            cx, cy = int(m.group(1)), int(m.group(2))
            data = base64.b64decode(m.group(3))
            n = len(data) // 7
            for k in range(n):
                tid = struct.unpack_from("<i", data, k * 7)[0]
                if tid == 0:
                    continue
                x = k % CHUNK
                y = k // CHUNK
                self.floor.add((cx * CHUNK + x, cy * CHUNK + y))

    # ---- queries -------------------------------------------------------------------------------------------
    def index(self):
        self.at = defaultdict(list)
        for proto, uid, pos, parent, _, _ in self.entities:
            if parent != self.grid or pos is None:
                continue
            self.at[tile_of(pos)].append(proto)

    def find(self, proto):
        return [(uid, pos) for p, uid, pos, parent, _, _ in self.entities if p == proto and parent == self.grid and pos]

    def is_wallish(self, t):
        return any(any(p.startswith(w) or w in p for w in WALLISH) for p in self.at.get(t, ()))

    def is_door(self, t):
        return any(any(d in p for d in DOORISH) for p in self.at.get(t, ()))

    def blocked(self, t, allow_table=False):
        if t not in self.floor:
            return True
        for p in self.at.get(t, ()):
            if allow_table and p.startswith("Table"):
                continue
            if any(p.startswith(w) or w in p for w in WALLISH):
                return True
            if any(p.startswith(a) for a in PASSABLE):
                continue
            return True
        return False

    def has_table(self, t):
        return any(p.startswith("Table") for p in self.at.get(t, ()))

    MACHINE_ON_TABLE = ("Fax", "Kitchen", "Computer", "Vending", "Rack", "Microwave", "Reagent", "Chem", "Soda",
                        "Beer", "Dispenser", "Drink" "Booze", "Juicer", "Grinder", "Cash", "Telephone", "Lathe",
                        "Printer", "Monitor", "Jukebox", "Griddle", "Oven", "Hotplate", "Cabinet")

    def table_free(self, t):
        """A table tile that is not crowded: at most two small items and no machine on it."""
        if not self.has_table(t):
            return False
        items = 0
        for p in self.at.get(t, ()):
            if p.startswith("Table"):
                continue
            if any(p.startswith(a) for a in PASSABLE):
                continue
            if any(p.startswith(m) for m in self.MACHINE_ON_TABLE):
                return False
            items += 1
        return items <= 2

    def lv_near(self, t, r=3):
        for dx in range(-r, r + 1):
            for dy in range(-r, r + 1):
                for p in self.at.get((t[0] + dx, t[1] + dy), ()):
                    if p == "CableApcExtension":
                        return True
        return False

    def neighbors(self, t):
        x, y = t
        return [(x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)]

    def score(self, t):
        """Higher is better: against a wall, not in a doorway, powered, not in a corridor junction."""
        s = 0
        walls = sum(1 for n in self.neighbors(t) if self.is_wallish(n) and not self.is_door(n))
        s += 3 if walls == 1 else (1 if walls == 2 else 0)
        if any(self.is_door(n) for n in self.neighbors(t)):
            s -= 6
        for n in self.neighbors(t):
            for nn in self.neighbors(n):
                if self.is_door(nn):
                    s -= 1
        if self.lv_near(t):
            s += 2
        free = sum(1 for n in self.neighbors(t) if not self.blocked(n))
        if free == 0:
            s -= 10
        return s

    def room(self, origin, radius):
        """Floor tiles reachable from the origin without passing doors or blocking furniture (same room)."""
        start = tile_of(origin)
        seen = {start}
        frontier = [start]
        while frontier:
            t = frontier.pop()
            for n in self.neighbors(t):
                if n in seen or abs(n[0] - start[0]) > radius or abs(n[1] - start[1]) > radius:
                    continue
                if n not in self.floor or self.is_door(n) or self.is_wallish(n):
                    continue
                seen.add(n)
                frontier.append(n)
        return seen

    def free_near(self, origin, radius, taken, allow_table=False, want_table=False):
        best = None
        best_s = -99
        ox, oy = tile_of(origin)
        room = self.room(origin, radius)
        for dx in range(-radius, radius + 1):
            for dy in range(-radius, radius + 1):
                t = (ox + dx, oy + dy)
                if t in taken or t not in room:
                    continue
                if want_table:
                    if not self.table_free(t):
                        continue
                    s = -abs(dx) - abs(dy)
                else:
                    if self.blocked(t, allow_table):
                        continue
                    s = self.score(t) - 0.3 * (abs(dx) + abs(dy))
                if s > best_s:
                    best, best_s = t, s
        return best

    # ---- writing -------------------------------------------------------------------------------------------
    def add_entity(self, proto, tile, extra_yaml=""):
        self.max_uid += 1
        uid = self.max_uid
        x, y = tile[0] + 0.5, tile[1] + 0.5
        pos = f"{x:g},{y:g}" if x != int(x) else f"{x:.1f},{y:.1f}"
        pos = f"{tile[0] + 0.5},{tile[1] + 0.5}"
        entry = (f"  - uid: {uid}\n    components:\n    - type: Transform\n      parent: {self.grid}\n      pos: {pos}\n" + extra_yaml)
        marker = f"- proto: {proto}\n  entities:\n"
        idx = self.text.find("\n" + marker)
        if idx == -1:
            # insert a new proto block in alphabetical position among "- proto:" blocks (before the first that sorts after)
            blocks = [(m.start(), m.group(1)) for m in re.finditer(r"^- proto: (\S+)$", self.text, re.M)]
            insert_at = None
            for start, name in blocks:
                if name.lower() > proto.lower():
                    insert_at = start
                    break
            block = marker + entry
            if insert_at is None:
                insert_at = self.text.index("\n", blocks[-1][0])  # fallback: after the last block start line
                # find end of last block: next top-level key
                m = re.search(r"^\S", self.text[blocks[-1][0] + 1:], re.M)
                insert_at = blocks[-1][0] + 1 + (m.start() if m else len(self.text) - blocks[-1][0] - 1)
            self.text = self.text[:insert_at] + block + self.text[insert_at:]
        else:
            at = idx + 1 + len(marker)
            self.text = self.text[:at] + entry + self.text[at:]
        self.at[tile].append(proto)
        self.entities.append((proto, uid, (tile[0] + 0.5, tile[1] + 0.5), self.grid, -1, None))
        return uid

    def bump_count(self, n):
        self.text = re.sub(r"^  entityCount: (\d+)$", lambda m: f"  entityCount: {int(m.group(1)) + n}", self.text, count=1, flags=re.M)

    def save(self):
        open(self.path, "w", encoding="utf-8", newline="\n").write(self.text)


def dist(a, b):
    return abs(a[0] - b[0]) + abs(a[1] - b[1])


def plan(mapfile: MapFile, log):
    m = mapfile
    m.index()
    taken = set()
    placed = []

    def all_pos(proto):
        return [pos for _, pos in m.find(proto)]

    def room_score(origin, bonus_protos):
        room = m.room(origin, 8)
        free = sum(1 for t in room if not m.blocked(t))
        bonus = 0
        for t in room:
            for p in m.at.get(t, ()):
                for name, val in bonus_protos.items():
                    if p.startswith(name):
                        bonus += val
        return free + bonus

    def best_anchor(proto, bonus_protos):
        cands = all_pos(proto)
        if not cands:
            return None
        return max(cands, key=lambda pos: room_score(pos, bonus_protos))

    def place(proto, anchors, radius=5, allow_table=False, want_table=False, extra="", label=""):
        anchors = [a for a in anchors if a is not None]
        if not anchors:
            log(f"  skip {proto}: no anchor")
            return None
        for r in (radius, radius + 4):
            for anchor in anchors:
                t = m.free_near(anchor, r, taken, allow_table, want_table)
                if t is None:
                    continue
                taken.add(t)
                uid = m.add_entity(proto, t, extra)
                placed.append((proto, t))
                log(f"  {proto:28s} @ {t[0]},{t[1]} {label}")
                return uid
        log(f"  skip {proto}: no free tile near {[tile_of(a) for a in anchors]}")
        return None

    def exists(proto):
        return len(m.find(proto)) > 0

    def first(proto):
        f = m.find(proto)
        return f[0][1] if f else None

    arcade_bonus = {"ArcadeMachine": 25, "SpaceVillain": 25, "BlockGame": 25, "StoolBar": 5, "VendingMachineBooze": 15, "Jukebox": 15, "SpawnPoint": -8, "Locker": -6}
    games = best_anchor("VendingMachineGames", arcade_bonus)
    booze = best_anchor("VendingMachineBooze", {"StoolBar": 3, "VendingMachineGames": 4, "Jukebox": 4})
    chef = best_anchor("VendingMachineChefvend", {"KitchenMicrowave": 3, "KitchenReagentGrinder": 2}) or first("KitchenMicrowave")
    records = best_anchor("ComputerCriminalRecords", {"LockerWardenFilled": 40, "ComputerCargoOrdersSecurity": 25, "SpawnPointWarden": 40, "SpawnPointSecurityOfficer": 15, "VendingMachineSec": 25, "ComputerCrewMonitoring": -10, "LockerHeadOfPersonnel": -40}) or first("ComputerCriminalRecords")
    hop = first("LockerHeadOfPersonnelFilled")
    cargo = first("ComputerCargoOrders")
    svc = first("ComputerCargoOrdersService")
    def any_of(*protos):
        for p in protos:
            f = first(p)
            if f is not None:
                return f
        return None

    eng = any_of("ComputerCargoOrdersEngineering", "ComputerPowerMonitoringConsole", "LockerChiefEngineerFilledHardsuit",
                 "LockerChiefEngineerFilled", "ComputerAtmosMonitoring", "LockerEngineerFilledHardsuit", "LockerEngineerFilled")
    med = any_of("ComputerCargoOrdersMedical", "LockerChiefMedicalOfficerFilledHardsuit", "LockerChiefMedicalOfficerFilled",
                 "VendingMachineMedical", "LockerMedicalFilled") \
          or best_anchor("ComputerMedicalRecords", {"MedicalBed": 2, "VendingMachineMedical": 4})
    sci = any_of("ComputerCargoOrdersScience", "ComputerResearchAndDevelopment", "LockerResearchDirectorFilledHardsuit",
                 "LockerResearchDirectorFilled", "LockerScienceFilled", "ComputerAnalysisConsole")
    sec = first("ComputerCargoOrdersSecurity") or records
    telepad = m.find("CargoTelepad")

    # Casino corner: ATM, chip exchanger and slot machines in the arcade / bar.
    casino = [games, booze]
    if not exists("ATM"):
        place("ATM", casino, 6, label="(casino / bar)")
    if not exists("ChipExchanger"):
        place("ChipExchanger", casino, 6)
    if not exists("SlotMachine"):
        place("SlotMachine", casino, 7)
        place("SlotMachine", casino, 7)
    if not exists("SlotMachineHighRoller"):
        place("SlotMachineHighRoller", casino, 7)

    # Second ATM in a different public spot (service orders / HoP line).
    atms = [t for p, t in placed if p == "ATM"]
    for anchor in (svc, hop):
        if anchor is not None and all(dist(tile_of(anchor), t) > 14 for t in atms):
            place("ATM", [anchor], 6, label="(public, second)")
            break

    # Registers on the bar and kitchen counters.
    if not exists("CashRegister"):
        if place("CashRegister", [booze], 7, want_table=True, label="(bar counter)") is None:
            place("CashRegister", [booze], 5, label="(bar, floor)")
        if chef is not None and (booze is None or dist(tile_of(chef), tile_of(booze)) > 4):
            if place("CashRegister", [chef], 7, want_table=True, label="(kitchen counter)") is None:
                place("CashRegister", [chef], 5, label="(kitchen, floor)")

    # Legal terminal + security board at the records console.
    if not exists("FineTerminal"):
        place("FineTerminal", [records], 5)
    if not exists("TaskBoardSecurity"):
        place("TaskBoardSecurity", [sec, records], 5)

    # Department boards.
    if not exists("TaskBoard"):
        place("TaskBoard", [svc, booze, hop], 6, label="(service)")
    if not exists("TaskBoardCargo"):
        place("TaskBoardCargo", [cargo], 5)
    if not exists("TaskBoardEngineering"):
        place("TaskBoardEngineering", [eng], 5)
    if not exists("TaskBoardMedical"):
        place("TaskBoardMedical", [med], 5)
    if not exists("TaskBoardScience"):
        place("TaskBoardScience", [sci], 5)

    # Service order console where missing.
    if not exists("ComputerCargoOrdersService") and (hop is not None or booze is not None):
        extra = ""
        if telepad:
            extra = (f"    - type: DeviceLinkSource\n      linkedPorts:\n        {telepad[0][0]}:\n        - - OrderSender\n          - OrderReceiver\n")
        place("ComputerCargoOrdersService", [hop, booze], 6, extra=extra, label="(service orders)")

    m.bump_count(len(placed))
    return placed


if __name__ == "__main__":
    dry = "--dry" in sys.argv
    paths = [a for a in sys.argv[1:] if not a.startswith("--")]
    for path in paths:
        print(f"== {path}")
        mf = MapFile(path)
        placed = plan(mf, print)
        if not dry and placed:
            mf.save()
        print(f"  -> {len(placed)} entities")
