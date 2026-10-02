#!/usr/bin/env python3
"""iss14: generates Resources/Maps/cloud.yml — "Cloud Station", a compact Box-style station for low/mid pop.

Layout is a central bar block inside a ring corridor, departments boxed around it, command to the north,
service/dorms north-west, arrivals + evac lobby north-east, engineering/atmos west, medbay/science east,
security/cargo south. Everything (walls, doors, lights, APCs, LV/MV/HV cabling, distro/waste piping, vents,
scrubbers, air alarms, firelocks, air, spawn points, beacons) is derived from the room list below, so the map can
be regenerated after editing this file. The output is a normal map file: open it in the editor and refine.
"""
import base64, math, struct, sys
from collections import defaultdict

PI = math.pi
HALF = PI / 2
ROT = {"S": None, "E": "1.5707963267948966 rad", "N": "3.141592653589793 rad", "W": "-1.5707963267948966 rad"}
# facing -> rot (rot 0 faces south). Wall-mounted on a floor tile: back to the wall, face away from it.
OPPOSITE = {"N": "S", "S": "N", "E": "W", "W": "E"}
DIRS = {"N": (0, 1), "S": (0, -1), "E": (1, 0), "W": (-1, 0)}

TILES = {"Space": 0, "Plating": 1, "FloorSteel": 2, "FloorDark": 3, "FloorWhite": 4, "FloorWood": 5, "FloorKitchen": 6,
         "FloorHydro": 7, "FloorBlueCircuit": 8, "FloorTechMaint": 9, "FloorReinforced": 10, "Lattice": 11,
         "FloorSteelMini": 12, "FloorBar": 13, "FloorArcadeBlue": 14, "FloorDarkMini": 15, "FloorWhiteMini": 16,
         "FloorSteelCheckerDark": 17, "FloorGrass": 18, "FloorFreezer": 19, "FloorLino": 20, "FloorCarpetOffice": 21,
         "FloorShowroom": 22, "FloorGold": 23, "FloorMetalDiamond": 24}


class Gen:
    def __init__(self):
        self.tiles = {}            # (x,y) -> tile name
        self.walls = {}            # (x,y) -> wall proto
        self.ents = []             # (proto, x, y, rot, extra_lines)
        self.rooms = []            # Room objects
        self.occupied = set()      # tiles with blocking furniture
        self.lv = set()
        self.mv = set()
        self.hv = set()
        self.distro = set()
        self.waste = set()
        self.attached = {False: {}, True: {}}   # distro/waste: pipe tile -> directions of attached devices
        self.air = set()           # tiles that get breathable air
        self.door_tiles = {}

    # ---- geometry --------------------------------------------------------------------------------------------
    def room(self, name, x0, y0, x1, y1, floor="FloorSteel", kind="room", access=None, dept=None):
        r = Room(name, x0, y0, x1, y1, floor, kind, access, dept)
        self.rooms.append(r)
        for x in range(x0, x1 + 1):
            for y in range(y0, y1 + 1):
                self.tiles[(x, y)] = floor
                self.air.add((x, y))
        return r

    def finalize_walls(self):
        """Every tile adjacent to a room interior that isn't interior itself becomes a wall on plating."""
        interior = set(self.tiles)
        for (x, y) in list(interior):
            for dx in (-1, 0, 1):
                for dy in (-1, 0, 1):
                    t = (x + dx, y + dy)
                    if t not in interior and t not in self.walls:
                        self.walls[t] = "WallSolid"
        for t in self.walls:
            self.tiles[t] = "Plating"

    def exterior(self, t):
        """A wall tile with space on at least one side."""
        x, y = t
        for dx, dy in DIRS.values():
            n = (x + dx, y + dy)
            if n not in self.tiles:
                return True
        return False

    def wall_axis(self, t):
        """'h' if the wall line through t runs east-west, 'v' if north-south."""
        x, y = t
        h = ((x + 1, y) in self.walls or (x + 1, y) in self.door_tiles) + ((x - 1, y) in self.walls or (x - 1, y) in self.door_tiles)
        v = ((x, y + 1) in self.walls or (x, y + 1) in self.door_tiles) + ((x, y - 1) in self.walls or (x, y - 1) in self.door_tiles)
        return "h" if h >= v else "v"

    def room_side(self, t):
        """Direction from a wall tile towards the (first) adjacent room interior."""
        x, y = t
        for d, (dx, dy) in DIRS.items():
            n = (x + dx, y + dy)
            if n in self.tiles and n not in self.walls and n not in self.door_tiles:
                return d
        return "S"

    # ---- placement -------------------------------------------------------------------------------------------
    def add(self, proto, x, y, rot=None, extra="", block=True, anchored=False):
        if anchored:
            extra = "ANCHORED" + extra
        self.ents.append((proto, x, y, rot, extra))
        if block:
            self.occupied.add((x, y))

    def door(self, x, y, proto="AirlockGlass", firelock=True):
        self.door_tiles[(x, y)] = proto
        self.walls.pop((x, y), None)
        self.tiles[(x, y)] = "Plating"
        self.air.add((x, y))

    def window(self, x, y, proto="ReinforcedWindow"):
        self.walls[(x, y)] = "WINDOW:" + proto

    def windows_along(self, x0, y0, x1, y1, every=2, proto="ReinforcedWindow"):
        i = 0
        for x in range(x0, x1 + 1):
            for y in range(y0, y1 + 1):
                if (x, y) in self.walls and (x, y) not in self.door_tiles and self.exterior((x, y)):
                    if i % every == 0:
                        self.window(x, y, proto)
                    i += 1

    def wallmount(self, proto, x, y, extra=""):
        """Entity sitting on a wall tile (APC, air alarm, intercom) facing into the room."""
        side = self.room_side((x, y))
        self.add(proto, x, y, ROT[side], extra, block=False)

    def on_floor_at_wall(self, proto, x, y, wall_dir, extra="", block=False):
        """Entity on a floor tile with its back to the wall in direction wall_dir (lights, posters)."""
        self.add(proto, x, y, ROT[OPPOSITE[wall_dir]], extra, block=block)

    # ---- automatic furnishing ----------------------------------------------------------------------------------
    def auto_room(self, r):
        """Lights, APC, air alarm, vent, scrubber, LV cables, firelocks for one room."""
        # Free interior tiles adjacent to a wall, grouped by wall direction.
        for d, (dx, dy) in DIRS.items():
            line = []
            for x in range(r.x0, r.x1 + 1):
                for y in range(r.y0, r.y1 + 1):
                    w = (x + dx, y + dy)
                    if w in self.walls and (x, y) not in self.occupied and w not in self.door_tiles:
                        line.append((x, y))
            line.sort()
            # one light every 4 tiles along the wall, skipping the first
            for i, t in enumerate(line):
                if i % 4 == 2:
                    self.on_floor_at_wall("Poweredlight" if r.kind != "maint" else "PoweredSmallLight", t[0], t[1], d)

        # APC and air alarm on interior-facing wall tiles that are not doors/windows and not exterior if possible.
        wall_tiles = []
        for x in range(r.x0 - 1, r.x1 + 2):
            for y in range(r.y0 - 1, r.y1 + 2):
                t = (x, y)
                if t in self.walls and not self.walls[t].startswith("WINDOW") and t not in self.door_tiles:
                    # must be straight wall (not corner) and face this room
                    if (r.x0 <= x <= r.x1) != (r.y0 <= y <= r.y1):
                        # not next to a door
                        if not any((x + ddx, y + ddy) in self.door_tiles for ddx, ddy in DIRS.values()):
                            wall_tiles.append(t)
        wall_tiles.sort(key=lambda t: (self.exterior(t), t))
        used = set()

        def take():
            for t in wall_tiles:
                if t not in used and t not in self.mounted:
                    used.add(t)
                    self.mounted.add(t)
                    return t
            return None

        apc = take()
        if apc:
            self.wallmount("APCBasic", apc[0], apc[1])
            self.mv.add(apc)
            self.lv.add(apc)
            r.apc = apc
        alarm = take()
        if alarm:
            self.wallmount("AirAlarm", alarm[0], alarm[1])
        if r.kind == "room" and (r.x1 - r.x0) * (r.y1 - r.y0) >= 20:
            ext = take()
            if ext:
                self.wallmount("ExtinguisherCabinetFilled", ext[0], ext[1])

        # LV: rows every 3 tiles + one column, through the whole room, plus the tile in front of the APC.
        for y in range(r.y0, r.y1 + 1):
            if (y - r.y0) % 3 == 1:
                for x in range(r.x0, r.x1 + 1):
                    self.lv.add((x, y))
        cx = r.x0 + (r.x1 - r.x0) // 2
        for y in range(r.y0, r.y1 + 1):
            self.lv.add((cx, y))
        if apc:
            d = self.room_side(apc)
            front = (apc[0] + DIRS[d][0], apc[1] + DIRS[d][1])
            self.lv.add(front)
            # connect front to the column
            x, y = front
            step = 1 if cx > x else -1
            while x != cx:
                self.lv.add((x, y))
                x += step

        # Vent + scrubber: near opposite corners, on free tiles. The pipe starts on the tile the connector faces.
        free = [(x, y) for x in range(r.x0, r.x1 + 1) for y in range(r.y0, r.y1 + 1)
                if (x, y) not in self.occupied and (x, y) not in self.reserved]

        def connector(t):
            x, y = t
            if (x, y - 1) in free:
                return "S", (x, y - 1)
            if (x, y + 1) in free:
                return "N", (x, y + 1)
            if (x + 1, y) in free:
                return "E", (x + 1, y)
            return "W", (x - 1, y)

        def put(proto, t, net, lst, extra=""):
            d, c = connector(t)
            self.add(proto, t[0], t[1], ROT[d], extra, block=False)
            net.add(c)
            lst.append(c)
            # the pipe under the connector tile must open towards the device
            self.attached[net is self.waste].setdefault(c, set()).add(OPPOSITE[d])

        if free:
            vent = min(free, key=lambda t: (abs(t[0] - r.x0) + abs(t[1] - r.y1)))
            scrub = max(free, key=lambda t: (abs(t[0] - vent[0]) + abs(t[1] - vent[1])))
            put("GasVentPump", vent, self.distro, self.vents)
            if scrub != vent:
                put("GasVentScrubber", scrub, self.waste, self.scrubbers, "    - type: AtmosPipeLayers\n      pipeLayer: Secondary\n")
            if (r.x1 - r.x0 + 1) * (r.y1 - r.y0 + 1) > 60:
                vent2 = max(free, key=lambda t: (abs(t[0] - vent[0]) + abs(t[1] - vent[1])) - (3 if t == scrub else 0))
                if vent2 not in (vent, scrub):
                    put("GasVentPump", vent2, self.distro, self.vents)

    # ---- networks ----------------------------------------------------------------------------------------------
    @staticmethod
    def l_route(a, b, net):
        """Lay an L-shaped run from a to b (x first, then y)."""
        x, y = a
        step = 1 if b[0] > x else -1
        while x != b[0]:
            net.add((x, y))
            x += step
        step = 1 if b[1] > y else -1
        while y != b[1]:
            net.add((x, y))
            y += step
        net.add((x, y))

    def connect_to_spine(self, points, root, net, forbidden=()):
        """Dijkstra from each point to the component of `net` that contains `root`, reusing existing pipe/cable
        where possible and never passing through forbidden tiles (devices whose pipe node would clash)."""
        import heapq
        from collections import deque
        forbidden = set(forbidden)
        comp = {root}
        q = deque([root])
        while q:
            x, y = q.popleft()
            for dx, dy in DIRS.values():
                n = (x + dx, y + dy)
                if n in net and n not in comp:
                    comp.add(n)
                    q.append(n)
        for p in points:
            if p in comp:
                continue
            dist = {p: 0}
            prev = {}
            heap = [(0, p)]
            goal = None
            while heap:
                d, t = heapq.heappop(heap)
                if t in comp:
                    goal = t
                    break
                if d > dist.get(t, 1e9):
                    continue
                for dx, dy in DIRS.values():
                    n = (t[0] + dx, t[1] + dy)
                    if n not in self.tiles or n in forbidden:
                        continue
                    nd = d + (0.01 if n in net else 1)
                    if nd < dist.get(n, 1e9):
                        dist[n] = nd
                        prev[n] = t
                        heapq.heappush(heap, (nd, n))
            if goal is None:
                print("WARNING: could not connect", p)
                continue
            t = goal
            path = []
            while t != p:
                path.append(t)
                t = prev[t]
            path.append(p)
            for t in path:
                net.add(t)
                comp.add(t)
            # anything already in net that touches the new path is now connected too
            q = deque(path)
            while q:
                x, y = q.popleft()
                for dx, dy in DIRS.values():
                    n = (x + dx, y + dy)
                    if n in net and n not in comp:
                        comp.add(n)
                        q.append(n)

    @staticmethod
    def pipe_shape(t, net, extra_dirs):
        """Pipe prototype + rotation that opens exactly towards the neighbouring pipes / attached devices."""
        dirs = set(extra_dirs)
        for d, (dx, dy) in DIRS.items():
            if (t[0] + dx, t[1] + dy) in net:
                dirs.add(d)
        key = "".join(sorted(dirs))
        table = {
            "NS": ("GasPipeStraight", None), "EW": ("GasPipeStraight", ROT["E"]),
            "N": ("GasPipeStraight", None), "S": ("GasPipeStraight", None), "E": ("GasPipeStraight", ROT["E"]), "W": ("GasPipeStraight", ROT["E"]),
            "SW": ("GasPipeBend", None), "ES": ("GasPipeBend", ROT["E"]), "EN": ("GasPipeBend", ROT["N"]), "NW": ("GasPipeBend", ROT["W"]),
            "ESW": ("GasPipeTJunction", None), "ENS": ("GasPipeTJunction", ROT["E"]), "ENW": ("GasPipeTJunction", ROT["N"]), "NSW": ("GasPipeTJunction", ROT["W"]),
            "ENSW": ("GasPipeFourway", None), "": ("GasPipeFourway", None),
        }
        return table[key]

    # ---- serialization -----------------------------------------------------------------------------------------
    def chunks(self):
        by_chunk = defaultdict(dict)
        for (x, y), name in self.tiles.items():
            cx, cy = x // 16, y // 16
            by_chunk[(cx, cy)][(x - cx * 16, y - cy * 16)] = TILES[name]
        out = {}
        for (cx, cy), tiles in by_chunk.items():
            data = bytearray()
            for ly in range(16):
                for lx in range(16):
                    tid = tiles.get((lx, ly), 0)
                    data += struct.pack("<i", tid) + bytes([0, 0, 0])
            out[(cx, cy)] = base64.b64encode(bytes(data)).decode()
        return out

    def atmos(self):
        by_chunk = defaultdict(int)
        for (x, y) in self.air:
            cx, cy = x // 4, y // 4
            k = (x - cx * 4) + (y - cy * 4) * 4
            by_chunk[(cx, cy)] |= 1 << k
        return by_chunk

    def write(self, path):
        # materialize networks and walls as entities
        for t, w in self.walls.items():
            if w.startswith("WINDOW:"):
                self.ents.append(("Grille", t[0], t[1], None, ""))
                self.ents.append((w[7:], t[0], t[1], None, ""))
            else:
                self.ents.append((w, t[0], t[1], None, ""))
        for t, proto in self.door_tiles.items():
            axis = self.wall_axis(t)
            rot = None if axis == "h" else ROT["E"]
            if proto.startswith("AirlockExternalGlassShuttle"):
                # docks face space
                x, y = t
                for d, (dx, dy) in DIRS.items():
                    if (x + dx, y + dy) not in self.tiles:
                        rot = ROT[d]
            self.ents.append((proto, t[0], t[1], rot, ""))
        for t in self.lv:
            self.ents.append(("CableApcExtension", t[0], t[1], None, ""))
        for t in self.mv:
            self.ents.append(("CableMV", t[0], t[1], None, ""))
        for t in self.hv:
            self.ents.append(("CableHV", t[0], t[1], None, ""))
        for t in self.distro:
            proto, rot = self.pipe_shape(t, self.distro, self.attached[False].get(t, set()))
            self.ents.append((proto, t[0], t[1], rot, "    - type: AtmosPipeColor\n      color: '#0055CCFF'\n"))
        for t in self.waste:
            proto, rot = self.pipe_shape(t, self.waste, self.attached[True].get(t, set()))
            self.ents.append((proto, t[0], t[1], rot,
                              "    - type: AtmosPipeColor\n      color: '#990000FF'\n    - type: AtmosPipeLayers\n      pipeLayer: Secondary\n"))

        groups = defaultdict(list)
        for proto, x, y, rot, extra in self.ents:
            groups[proto].append((x, y, rot, extra))

        uid = 3
        lines = []
        total = 2
        for proto in sorted(groups, key=str.lower):
            lines.append(f"- proto: {proto}\n  entities:\n")
            for (x, y, rot, extra) in groups[proto]:
                lines.append(f"  - uid: {uid}\n    components:\n    - type: Transform\n")
                lines.append(f"      parent: 2\n      pos: {x + 0.5},{y + 0.5}\n")
                if rot:
                    lines.append(f"      rot: {rot}\n")
                if extra.startswith("ANCHORED"):
                    lines.append("      anchored: True\n")
                    extra = extra[8:]
                if extra:
                    lines.append(extra)
                uid += 1
                total += 1

        chunk_lines = []
        for (cx, cy), b64 in sorted(self.chunks().items()):
            chunk_lines.append(f"        {cx},{cy}:\n          ind: {cx},{cy}\n          tiles: {b64}\n          version: 7\n")
        atmos_lines = []
        for (cx, cy), mask in sorted(self.atmos().items()):
            atmos_lines.append(f"          {cx},{cy}:\n            1: {mask}\n")

        tilemap = "\n".join(f"  {v}: {k}" for k, v in sorted(TILES.items(), key=lambda kv: kv[1]))
        header = f"""meta:
  format: 7
  category: Map
  engineVersion: 291.0.0
  forkId: ""
  forkVersion: ""
  time: 10/02/2026 12:00:00
  entityCount: {total}
maps:
- 1
grids:
- 2
orphans: []
nullspace: []
tilemap:
{tilemap}
entities:
- proto: ""
  entities:
  - uid: 1
    components:
    - type: MetaData
    - type: Transform
    - type: Map
      mapPaused: True
    - type: GridTree
    - type: Broadphase
    - type: OccluderTree
  - uid: 2
    components:
    - type: MetaData
      name: Cloud Station
    - type: Transform
      parent: 1
    - type: MapGrid
      chunks:
{''.join(chunk_lines)}    - type: Broadphase
    - type: Physics
      bodyStatus: InAir
      angularDamping: 0.05
      linearDamping: 0.05
      fixedRotation: False
      bodyType: Dynamic
    - type: Fixtures
      fixtures: {{}}
    - type: BecomesStation
      id: Cloud
    - type: OccluderTree
    - type: Shuttle
    - type: GridPathfinding
    - type: Gravity
      gravityShakeSound: !type:SoundPathSpecifier
        path: /Audio/Effects/alert.ogg
    - type: DecalGrid
      chunkCollection:
        version: 2
        nodes: []
    - type: GridAtmosphere
      version: 2
      data:
        tiles:
{''.join(atmos_lines)}        uniqueMixes:
        - volume: 2500
          immutable: True
          moles: {{}}
        - volume: 2500
          temperature: 293.15
          moles:
            Oxygen: 21.824879
            Nitrogen: 82.10312
        chunkSize: 4
    - type: GasTileOverlay
    - type: RadiationGridResistance
    - type: SpreaderGrid
    - type: ImplicitRoof
    - type: ExplosionAirtightGrid
    - type: NavMap
    - type: TileHistory
      chunkHistory: {{}}
"""
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(header)
            f.write("".join(lines))
        return total


class Room:
    def __init__(self, name, x0, y0, x1, y1, floor, kind, access, dept):
        self.name, self.x0, self.y0, self.x1, self.y1 = name, x0, y0, x1, y1
        self.floor, self.kind, self.access, self.dept = floor, kind, access, dept
        self.apc = None

    @property
    def cx(self):
        return self.x0 + (self.x1 - self.x0) // 2

    @property
    def cy(self):
        return self.y0 + (self.y1 - self.y0) // 2


def build():
    g = Gen()
    g.mounted = set()
    g.vents = []
    g.scrubbers = []
    A = g.add

    # ======================================================================================= layout =============
    # Central block: bar (east) + kitchen (west).
    bar = g.room("Bar", -1, -3, 6, 3, "FloorWood")
    kitchen = g.room("Kitchen", -6, -3, -3, 3, "FloorKitchen")
    # Ring corridor
    ring_n = g.room("Ring N", -9, 5, 9, 6, "FloorSteel", kind="corridor")
    ring_s = g.room("Ring S", -9, -6, 9, -5, "FloorSteel", kind="corridor")
    ring_w = g.room("Ring W", -9, -4, -8, 4, "FloorSteel", kind="corridor")
    ring_e = g.room("Ring E", 8, -4, 9, 4, "FloorSteel", kind="corridor")
    # Command (north)
    cmd_hall = g.room("Command hall", -9, 8, 9, 10, "FloorDark", kind="corridor")
    bridge = g.room("Bridge", -3, 12, 3, 17, "FloorDark")
    captain = g.room("Captain's office", -9, 12, -5, 17, "FloorCarpetOffice")
    hop = g.room("HoP office", 5, 12, 9, 17, "FloorCarpetOffice")
    # West: engineering
    west_hall = g.room("West hall", -24, 8, -11, 10, "FloorSteel", kind="corridor")
    power = g.room("Power room", -24, 1, -18, 6, "FloorTechMaint")
    engi = g.room("Engineering", -16, 1, -11, 6, "FloorSteel")
    atmos = g.room("Atmospherics", -24, -10, -18, -1, "FloorTechMaint")
    tech = g.room("Tech storage", -16, -10, -11, -1, "FloorSteel")
    # North-west: service
    hydro = g.room("Hydroponics", -24, 12, -19, 18, "FloorHydro")
    jani = g.room("Janitor", -17, 12, -15, 14, "FloorSteelCheckerDark")
    dorms = g.room("Dorms", -17, 16, -11, 18, "FloorCarpetOffice")
    cryo = g.room("Cryo", -13, 12, -11, 14, "FloorWhite")
    # East: medical + science
    east_hall = g.room("East hall", 11, 8, 24, 10, "FloorSteel", kind="corridor")
    medbay = g.room("Medbay", 11, 1, 18, 6, "FloorWhite")
    chem = g.room("Chemistry", 20, 1, 24, 6, "FloorWhite")
    rnd = g.room("Research", 11, -6, 18, -1, "FloorDarkMini")
    rdoff = g.room("Anomaly lab", 20, -6, 24, -1, "FloorDark")
    arcade = g.room("Arcade", 11, -11, 18, -8, "FloorArcadeBlue")
    # North-east: arrivals / evac lobby
    lobby = g.room("Evac lobby", 11, 12, 24, 18, "FloorSteelMini")
    # South: security (west) / cargo (east)
    south_hall = g.room("South hall", -9, -10, 9, -8, "FloorSteel", kind="corridor")
    sec = g.room("Security", -9, -16, -1, -12, "FloorDark")
    brig = g.room("Brig", -9, -22, -5, -18, "FloorDark")
    armory = g.room("Armory", -3, -22, -1, -18, "FloorDark")
    cargo = g.room("Cargo bay", 1, -16, 9, -12, "FloorSteel")
    qm = g.room("QM office", 11, -16, 14, -13, "FloorCarpetOffice")
    salvage = g.room("Salvage", 16, -16, 18, -13, "FloorSteel")
    g.finalize_walls()

    # Doors -------------------------------------------------------------------------------------------------
    D = g.door
    D(7, 0, "AirlockGlass"); D(7, -2, "AirlockGlass")   # bar <-> ring e
    D(-2, 2, "AirlockKitchenGlassLocked")              # kitchen <-> bar
    D(-7, 0, "AirlockKitchenGlassLocked")              # ring w <-> kitchen
    D(2, -4, "AirlockGlass"); D(2, 4, "AirlockGlass")   # bar <-> ring s/n
    D(0, 7, "AirlockCommandGlassLocked"); D(-6, 7, "AirlockGlass"); D(6, 7, "AirlockGlass")
    D(0, 11, "AirlockCommandGlassLocked")
    D(-6, 11, "AirlockCaptainLocked")
    D(6, 11, "AirlockHeadOfPersonnelLocked")
    D(-10, 9, "AirlockGlass")                           # cmd hall <-> west hall
    D(-10, 3, "AirlockEngineeringGlassLocked")          # ring w <-> engineering
    D(-13, 7, "AirlockEngineeringGlassLocked")          # engineering <-> west hall
    D(-17, 3, "AirlockEngineeringGlassLocked")          # engineering <-> power room
    D(-10, -3, "AirlockEngineeringGlassLocked")         # ring w <-> tech storage
    D(-17, -5, "AirlockAtmosphericsGlassLocked")        # tech <-> atmos
    D(-13, 0, "AirlockEngineeringGlassLocked")          # engi <-> tech storage
    D(-21, 11, "AirlockHydroponicsGlass")               # west hall <-> hydro
    D(-16, 11, "AirlockJanitorLocked")                  # west hall <-> janitor
    D(-12, 11, "AirlockGlass")                          # west hall <-> cryo
    D(-12, 15, "AirlockGlass")                          # cryo <-> dorms
    D(10, 9, "AirlockGlass")                            # cmd hall <-> east hall
    D(10, 3, "AirlockMedicalGlassLocked")               # ring e <-> medbay
    D(14, 7, "AirlockMedicalGlassLocked")               # medbay <-> east hall
    D(19, 3, "AirlockChemistryGlassLocked")             # medbay <-> chem
    D(22, 7, "AirlockChemistryGlassLocked")             # chem <-> east hall
    D(10, -3, "AirlockScienceGlassLocked")              # ring e <-> research
    D(19, -3, "AirlockScienceGlassLocked")              # research <-> anomaly lab
    D(10, -9, "AirlockGlass")                           # south hall <-> arcade
    D(16, 11, "AirlockGlass"); D(20, 11, "AirlockGlass")
    D(25, 14, "AirlockExternalGlassShuttleEmergencyLocked"); D(25, 16, "AirlockExternalGlassShuttleEmergencyLocked")
    D(18, 19, "AirlockExternalGlassShuttleArrivals")
    D(-5, -7, "AirlockGlass"); D(5, -7, "AirlockGlass"); D(0, -7, "AirlockGlass")
    D(-5, -11, "AirlockSecurityGlassLocked")            # south hall <-> security
    D(-7, -17, "AirlockBrigGlassLocked")                # security <-> brig
    D(-2, -17, "AirlockArmoryLocked")                   # security <-> armory
    D(5, -11, "AirlockCargoGlassLocked")                # south hall <-> cargo
    D(10, -14, "AirlockQuartermasterLocked")            # cargo <-> QM
    D(15, -14, "AirlockSalvageLocked")                  # QM <-> salvage
    D(5, -17, "AirlockExternalGlassShuttleCargo"); D(3, -17, "AirlockExternalGlassShuttleCargo")

    # Windows on exterior walls (every 2nd tile) ------------------------------------------------------------
    g.windows_along(-26, -24, 26, 20, every=2)
    # interior windows: bar <-> ring, bridge front, medbay front
    for x in range(-1, 7):
        if x % 2 == 0 and (x, 5) not in g.door_tiles and (x, -5) not in g.door_tiles:
            pass
    for (x, y) in [(-2, 12), (2, 12)]:
        pass

    # ================================================================================== furnishing ============
    # Bar
    for x in range(0, 5):
        A("TableCounterWood", x, 1)
        A("StoolBar", x, 0, block=False)
    A("VendingMachineBooze", 0, 3); A("BoozeDispenser", 1, 3); A("SodaDispenser", 2, 3); A("VendingMachineCigs", 3, 3)
    A("TableCounterWood", 5, 2); A("CashRegister", 5, 2, block=False); A("TableCounterWood", 5, 1)
    A("Jukebox", 6, 3); A("LockerBoozeFilled", 6, 2)
    A("TableWood", 1, -2); A("TableWood", 2, -2); A("ChairWood", 1, -3, ROT["N"], block=False)
    A("ChairWood", 2, -3, ROT["N"], block=False); A("ChairWood", 1, -1, None, block=False); A("ChairWood", 2, -1, None, block=False)
    A("TableWood", 5, -2); A("ChairWood", 4, -2, ROT["E"], block=False); A("ChairWood", 6, -2, ROT["W"], block=False)
    A("PottedPlantRandom", -1, 3); A("PottedPlantRandom", -1, -3)
    A("SpawnPointBartender", 3, 2, block=False); A("SpawnPointPassenger", 4, -1, block=False); A("SpawnPointPassenger", 0, -1, block=False)
    A("SpawnPointMusician", 5, 0, block=False); A("SpawnPointServiceWorker", 4, 2, block=False); A("SpawnPointClown", 3, -1, block=False); A("SpawnPointMime", 6, 0, block=False)
    A("DefaultStationBeaconBar", 3, -3, block=False); A("TaskBoard", 6, -3); A("ATM", -1, 0)
    # Kitchen
    A("KitchenMicrowave", -6, 3); A("KitchenReagentGrinder", -5, 3); A("KitchenElectricGrill", -4, 3); A("VendingMachineChefvend", -3, 3)
    A("VendingMachineDinnerware", -6, -3); A("VendingMachineCondiments", -5, -3); A("ClosetChefFilled", -4, -3); A("KitchenSpike", -3, -3)
    A("TableCounterMetal", -3, 1); A("TableCounterMetal", -3, 0); A("CashRegister", -3, 0, block=False); A("TableCounterMetal", -3, -1)
    A("Table", -6, 1); A("Table", -6, 0); A("Sink", -6, 2, ROT["E"], block=False); A("SpawnPointChef", -5, 0, block=False)
    A("DefaultStationBeaconKitchen", -4, 2, block=False)
    # Ring: vending + signs
    A("VendingMachineCola", -9, 6); A("VendingMachineSnack", -8, 6); A("VendingMachineCoffee", 9, 6); A("VendingMachineCigs", 9, -6)
    A("StationMap", -9, -6, ROT["N"], block=False); A("ATM", 8, -6)
    A("DefaultStationBeaconService", -8, 5, block=False)
    for (x, y) in [(-4, 6), (4, 6), (-4, -6), (4, -6)]:
        A("SpawnPointLatejoin", x, y, block=False)
    # Command hall
    A("ComputerId", 8, 10); A("TableGlass", 7, 10); A("PottedPlantRandom", -9, 10); A("PottedPlantRandom", 9, 8)
    A("DefaultStationBeaconHOPOffice", 7, 14, block=False)
    A("VendingMachineClothing", -8, 10); A("VendingMachineWinter", -9, 8)
    # Bridge
    A("ComputerComms", 0, 17); A("ComputerAlert", -1, 17); A("ComputerCrewMonitoring", 1, 17); A("ComputerStationRecords", -2, 17)
    A("ComputerPowerMonitoring", 2, 17); A("ComputerSurveillanceCameraMonitor", 3, 17); A("ComputerCargoOrders", -3, 17)
    A("ComputerEmergencyShuttle", -3, 16); A("ComputerRadar", 3, 16); A("ComputerShuttle", 3, 15)
    A("ChairOfficeDark", 0, 16, ROT["N"], block=False); A("ChairOfficeDark", -1, 16, ROT["N"], block=False); A("ChairOfficeDark", 1, 16, ROT["N"], block=False)
    A("TableGlass", -1, 13); A("TableGlass", 0, 13); A("TableGlass", 1, 13)
    A("ChairOfficeDark", -1, 12, ROT["N"], block=False); A("ChairOfficeDark", 1, 12, ROT["N"], block=False); A("ChairOfficeDark", 0, 14, None, block=False)
    A("FaxMachineBase", -3, 13); A("TelecomServerFilled", 3, 13); A("CrewMonitoringServer", 3, 12)
    A("SpawnPointCaptain", 0, 15, block=False); A("DefaultStationBeaconBridge", -2, 15, block=False); A("IntercomCommand", -3, 12, block=False)
    A("NuclearBomb", -3, 14)
    # Captain
    A("LockerCaptainFilledHardsuit", -9, 17); A("SuitStorageCaptain", -8, 17); A("TableWood", -7, 15); A("ComfyChair", -7, 16, ROT["N"], block=False)
    A("ComputerComms", -5, 17); A("Bed", -9, 13); A("BedsheetCaptain", -9, 13, block=False); A("LockerFreezerVaultFilled", -5, 13)
    A("SpawnMobFoxRenault", -6, 14, block=False); A("DefaultStationBeaconCaptainsQuarters", -7, 13, block=False)
    # HoP
    A("LockerHeadOfPersonnelFilled", 9, 17); A("ComputerId", 7, 17); A("TableWood", 7, 15); A("ComfyChair", 7, 16, ROT["N"], block=False)
    A("ComputerCargoOrdersService", 5, 17); A("SpawnPointHeadOfPersonnel", 7, 14, block=False); A("SpawnMobCorgi", 8, 13, block=False)
    A("FaxMachineBase", 9, 13); A("ATM", 5, 13); A("TaskBoard", 9, 14)
    # Engineering
    A("LockerChiefEngineerFilledHardsuit", -16, 6); A("LockerEngineerFilledHardsuit", -15, 6); A("LockerEngineerFilledHardsuit", -14, 6)
    A("SuitStorageCE", -13, 6); A("VendingMachineEngivend", -12, 6); A("VendingMachineYouTool", -11, 6)
    A("ComputerPowerMonitoring", -16, 2); A("ComputerAtmosMonitoring", -16, 1); A("ComputerCargoOrdersEngineering", -11, 1)
    A("TaskBoardEngineering", -11, 2); A("Autolathe", -11, 4); A("Table", -14, 3); A("Table", -13, 3)
    A("SpawnPointChiefEngineer", -15, 2, block=False); A("SpawnPointStationEngineer", -14, 2, block=False); A("SpawnPointStationEngineer", -13, 2, block=False)
    A("SpawnPointTechnicalAssistant", -12, 2, block=False); A("DefaultStationBeaconEngineering", -14, 4, block=False)
    A("ClosetToolFilled", -16, 4); A("CrateFilledSpawner", -12, 4)
    # Power room: generators on HV, SMES, substation, gravity gen
    for y in range(2, 6):
        A("GeneratorBasic15kW", -24, y); g.hv.add((-24, y))
    for y in range(2, 6):
        A("GeneratorBasic15kW", -22, y); g.hv.add((-22, y))
    for x in range(-24, -19):
        g.hv.add((x, 3))
    A("CableTerminal", -20, 3, ROT["E"], block=False)
    A("SMESAdvanced", -19, 3); g.hv.add((-19, 3))
    g.hv.add((-19, 4)); g.hv.add((-19, 5)); g.hv.add((-18, 5))
    A("SubstationBasic", -18, 5)
    sub_west = (-18, 5)
    g.hv.add((-19, 2)); g.hv.add((-19, 1)); g.hv.add((-18, 1)); g.hv.add((-18, 1))
    A("GravityGeneratorMini", -18, 1)
    A("SpawnPointAtmos", -21, 1, block=False); A("DefaultStationBeaconPowerBank", -21, 5, block=False)
    A("ClosetRadiationSuitFilled", -24, 1); A("ClosetEmergencyFilledRandom", -24, 6)
    # Atmospherics: canisters on ports feed distro; waste passive vent outside
    A("GasPort", -24, -2, ROT["E"], block=False); A("AirCanister", -24, -2, None,
      "    - type: GasCanister\n      releaseValveOpen: True\n      releasePressure: 303.975\n", anchored=True)
    A("GasPort", -24, -4, ROT["E"], block=False); A("AirCanister", -24, -4, None,
      "    - type: GasCanister\n      releaseValveOpen: True\n      releasePressure: 303.975\n", anchored=True)
    g.distro.add((-23, -2)); g.distro.add((-23, -3)); g.distro.add((-23, -4))
    g.attached[False].setdefault((-23, -2), set()).add("W"); g.attached[False].setdefault((-23, -4), set()).add("W")
    A("OxygenCanister", -24, -7); A("NitrogenCanister", -24, -8); A("AirCanister", -24, -9); A("AirCanister", -23, -9)
    A("LockerAtmosphericsFilledHardsuit", -18, -1); A("SuitStorageAtmos", -19, -1); A("VendingMachineTankDispenserEngineering", -18, -10)
    A("ComputerAtmosMonitoring", -20, -1); A("SpawnPointAtmos", -21, -5, block=False); A("DefaultStationBeaconAtmospherics", -20, -6, block=False)
    A("PortableScrubber", -18, -8); A("SpaceHeater", -18, -9)
    g.tiles[(-26, -6)] = "Lattice"
    A("GasPassiveVent", -26, -6, ROT["E"], "    - type: AtmosPipeLayers\n      pipeLayer: Secondary\n", block=False)
    g.waste.add((-25, -6)); g.waste.add((-24, -6)); g.waste.add((-23, -6))
    g.attached[True].setdefault((-25, -6), set()).add("W")
    # Tech storage
    A("CrateFilledSpawner", -16, -10); A("CrateFilledSpawner", -15, -10); A("Rack", -12, -10); A("Rack", -11, -10)
    A("ClosetToolFilled", -11, -2); A("ClosetFireFilled", -16, -2); A("Table", -14, -6); A("Table", -13, -6)
    A("VendingMachineTankDispenserEVA", -11, -5); A("SuitStorageEVA", -11, -7); A("SuitStorageEVA", -11, -8)
    # Hydroponics
    for x in range(-24, -19):
        A("hydroponicsTray", x, 18)
    for x in range(-24, -19):
        A("hydroponicsTray", x, 15)
    A("VendingMachineSeeds", -24, 12); A("VendingMachineNutri", -23, 12); A("SeedExtractor", -22, 12); A("WaterTankHighCapacity", -21, 12)
    A("LockerBotanistFilled", -19, 12); A("SpawnPointBotanist", -21, 16, block=False); A("DefaultStationBeaconBotany", -22, 13, block=False)
    # Janitor
    A("ClosetJanitorFilled", -17, 14); A("JanitorialTrolley", -16, 14); A("VendingMachineJaniDrobe", -15, 14); A("MopBucket", -17, 12)
    A("SpawnPointJanitor", -16, 13, block=False); A("DefaultStationBeaconJanitorsCloset", -15, 13, block=False)
    # Cryo + dorms
    A("CryogenicSleepUnitSpawnerLateJoin", -13, 14); A("CryogenicSleepUnitSpawnerLateJoin", -11, 14); A("CryogenicSleepUnit", -12, 14)
    A("DefaultStationBeaconCryonics", -12, 12, block=False)
    for x in (-17, -15, -13, -11):
        A("Bed", x, 18); A("BedsheetMedical", x, 18, block=False)
    A("VendingMachineCart", -17, 16); A("SpawnPointPassenger", -14, 17, block=False); A("DefaultStationBeaconDorms", -14, 16, block=False)
    A("ClosetWallEmergencyFilledRandom", -11, 16)
    # Medbay
    A("MedicalScanner", 11, 6); A("CryoPod", 12, 6); A("StasisBed", 14, 6); A("MedicalBed", 16, 6); A("MedicalBed", 18, 6)
    A("VendingMachineMedical", 11, 1); A("VendingMachineMediDrobe", 12, 1); A("LockerMedicalFilled", 13, 1); A("LockerMedicineFilled", 14, 1)
    A("LockerChiefMedicalOfficerFilledHardsuit", 18, 1); A("SuitStorageCMO", 17, 1); A("ComputerMedicalRecords", 16, 1); A("ComputerCargoOrdersMedical", 15, 1)
    A("TaskBoardMedical", 18, 3); A("MedicalTechFab", 18, 4); A("Table", 14, 3); A("Table", 15, 3); A("DefibrillatorCabinetFilled", 11, 3)
    A("SpawnPointChiefMedicalOfficer", 16, 4, block=False); A("SpawnPointMedicalDoctor", 13, 4, block=False); A("SpawnPointMedicalDoctor", 14, 4, block=False)
    A("SpawnPointParamedic", 12, 4, block=False); A("SpawnPointMedicalIntern", 15, 4, block=False); A("DefaultStationBeaconMedbay", 13, 2, block=False)
    # Chemistry
    A("ChemDispenser", 20, 6); A("ChemMaster", 21, 6); A("MachineCentrifuge", 22, 6); A("VendingMachineChemicals", 24, 6)
    A("LockerChemistryFilled", 24, 1); A("Table", 22, 3); A("Table", 21, 3); A("ChemistryHotplate", 21, 3, block=False)
    A("SpawnPointChemist", 22, 2, block=False); A("DefaultStationBeaconChemistry", 20, 2, block=False); A("Sink", 20, 1, ROT["E"], block=False)
    # Research
    A("ResearchAndDevelopmentServer", 18, -6); A("ComputerResearchAndDevelopment", 17, -6); A("Protolathe", 11, -6); A("CircuitImprinter", 12, -6)
    A("VendingMachineSciDrobe", 11, -1); A("LockerScienceFilled", 12, -1); A("LockerResearchDirectorFilledHardsuit", 18, -1); A("SuitStorageRD", 17, -1)
    A("ComputerCargoOrdersScience", 16, -1); A("TaskBoardScience", 15, -1); A("Table", 14, -4); A("Table", 15, -4)
    A("SpawnPointResearchDirector", 16, -3, block=False); A("SpawnPointScientist", 13, -3, block=False); A("SpawnPointScientist", 14, -3, block=False)
    A("SpawnPointResearchAssistant", 15, -5, block=False); A("DefaultStationBeaconRND", 13, -5, block=False); A("MachineArtifactAnalyzer", 14, -6)
    A("ComputerAnalysisConsole", 13, -6)
    # Anomaly lab
    A("MachineAnomalyGenerator", 22, -6); A("MachineAnomalyVessel", 20, -1); A("MachineAnomalyVessel", 24, -1)
    A("DefaultStationBeaconAnomalyGenerator", 22, -3, block=False); A("MachineAPE", 21, -4); A("MachineAPE", 23, -4)
    # Evac lobby: docks on the east wall, arrivals north
    for x in range(12, 17):
        A("Chair", x, 13, ROT["N"], block=False)
    A("ArrivalsShuttleTimer", 18, 18, ROT["S"], block=False); A("ScreenTimer", 24, 15, ROT["W"], block=False)
    A("ATM", 11, 18); A("VendingMachineCola", 12, 18); A("VendingMachineSnack", 13, 18); A("PottedPlantRandom", 23, 12); A("PottedPlantRandom", 23, 18)
    A("SpawnPointObserver", 17, 15, block=False); A("DefaultStationBeaconEvac", 22, 15, block=False); A("DefaultStationBeaconArrivals", 18, 17, block=False)
    for (x, y) in [(14, 16), (19, 14), (21, 17)]:
        A("SpawnPointLatejoin", x, y, block=False)
    A("ClosetEmergencyFilledRandom", 11, 12); A("ClosetFireFilled", 11, 13)
    # Security
    A("LockerHeadOfSecurityFilledHardsuit", -9, -12); A("SuitStorageHOS", -8, -12); A("LockerWardenFilled", -7, -12); A("LockerSecurityFilled", -6, -12)
    A("LockerSecurityFilled", -5, -12); A("VendingMachineSec", -4, -12); A("VendingMachineSecDrobe", -3, -12); A("WeaponCapacitorRecharger", -1, -12)
    A("ComputerCriminalRecords", -9, -16); A("FineTerminal", -8, -16); A("TaskBoardSecurity", -7, -16); A("ComputerCargoOrdersSecurity", -6, -16)
    A("ComputerSurveillanceCameraMonitor", -4, -16); A("LockerEvidence", -1, -16); A("ClosetLegalFilled", -2, -16); A("LockerDetectiveFilled", -3, -16)
    A("Table", -6, -14); A("Table", -5, -14); A("ChairOfficeDark", -6, -13, ROT["N"], block=False); A("ChairOfficeDark", -5, -15, None, block=False)
    A("SpawnPointHeadOfSecurity", -8, -14, block=False); A("SpawnPointWarden", -7, -14, block=False); A("SpawnPointSecurityOfficer", -3, -14, block=False)
    A("SpawnPointSecurityOfficer", -2, -14, block=False); A("SpawnPointDetective", -4, -15, block=False); A("SpawnPointLawyer", -1, -13, block=False)
    A("SpawnPointSecurityCadet", -2, -13, block=False); A("DefaultStationBeaconSecurity", -4, -13, block=False)
    # Brig
    A("LockerPrisoner", -9, -22); A("LockerPrisoner2", -8, -22); A("WardrobePrisonFilled", -7, -22); A("Bed", -9, -18); A("Bed", -5, -18); A("ToiletDirtyWater", -5, -22)
    A("TableReinforced", -7, -20); A("DefaultStationBeaconBrig", -7, -19, block=False)
    # Armory
    A("ClosetL3SecurityFilled", -3, -22); A("SuitStorageSec", -2, -22); A("SuitStorageSec", -1, -22); A("Rack", -3, -18); A("Rack", -1, -18)
    A("WallWeaponCapacitorRecharger", -2, -23, ROT["N"], block=False); A("DefaultStationBeaconArmory", -2, -20, block=False)
    # Cargo
    A("ComputerCargoOrders", 1, -12); A("ComputerCargoBounty", 2, -12); A("ComputerShuttleCargo", 3, -12); A("TaskBoardCargo", 4, -12)
    A("ComputerFundingAllocation", 5, -12); A("CargoTelepad", 7, -13); A("VendingMachineCargoDrobe", 9, -12); A("Recycler", 9, -14, ROT["W"])
    for x in range(1, 8):
        A("CargoPallet", x, -15, block=False)
    A("SpawnPointCargoTechnician", 3, -14, block=False); A("SpawnPointCargoTechnician", 4, -14, block=False); A("DefaultStationBeaconCargoBay", 5, -15, block=False)
    A("CrateFilledSpawner", 9, -16); A("CrateEmptySpawner", 8, -16); A("CrateEmptySpawner", 9, -15)
    # QM / salvage
    A("LockerQuarterMasterFilled", 11, -16); A("ComputerCargoOrders", 14, -16); A("TableWood", 12, -16); A("ComfyChair", 13, -16, ROT["W"], block=False)
    A("SpawnPointQuartermaster", 12, -14, block=False)
    A("LockerSalvageSpecialistFilled", 16, -16); A("SuitStorageSalv", 17, -16); A("VendingMachineSalvage", 18, -16); A("OreProcessor", 18, -13)
    A("ComputerSalvageExpedition", 16, -13); A("SpawnPointSalvageSpecialist", 17, -14, block=False); A("MachineMaterialSilo", 17, -13)
    # Arcade
    A("VendingMachineGames", 11, -8); A("SlotMachine", 12, -8); A("SlotMachine", 13, -8); A("SlotMachineHighRoller", 14, -8); A("ChipExchanger", 15, -8)
    A("ATM", 16, -8); A("RandomArcade", 18, -8); A("RandomArcade", 18, -9); A("RandomArcade", 18, -10)
    A("TableWood", 13, -10); A("TableWood", 14, -10); A("ChairWood", 13, -11, ROT["N"], block=False); A("ChairWood", 14, -11, ROT["N"], block=False)
    A("Jukebox", 11, -11); A("DefaultStationBeaconService", 16, -10, block=False)
    # South hall
    A("VendingMachineCigs", -9, -10); A("StationMap", 9, -8, ROT["S"], block=False); A("ATM", 9, -10)

    # ================================================================================= auto systems ============
    # MV spine: ring + halls, then every APC gets an L-run to it. Substation feeds the spine.
    spine = set()
    for x in range(-9, 10):
        spine.add((x, 5)); spine.add((x, -5)); spine.add((x, 9)); spine.add((x, -9))
    for y in range(-9, 10):
        spine.add((-8, y)); spine.add((8, y))
    for x in range(-24, 25):
        spine.add((x, 9))
    for y in range(-16, -9):
        spine.add((-8, y)); spine.add((8, y))
    g.reserved = set(spine)
    for r in g.rooms:
        g.auto_room(r)

    # Firelocks on the corridor side of every airlock that leads out of a corridor.
    corridor_tiles = set()
    for r in g.rooms:
        if r.kind == "corridor":
            for x in range(r.x0, r.x1 + 1):
                for y in range(r.y0, r.y1 + 1):
                    corridor_tiles.add((x, y))
    for (x, y), proto in g.door_tiles.items():
        if proto.startswith("AirlockExternal"):
            continue
        for d, (dx, dy) in DIRS.items():
            n = (x + dx, y + dy)
            if n in corridor_tiles and n not in g.occupied:
                A("FirelockGlass", n[0], n[1], None if g.wall_axis((x, y)) == "h" else ROT["E"], block=False)
                break

    g.mv |= spine
    g.l_route(sub_west, (-18, 9), g.mv)
    apcs = [r.apc for r in g.rooms if r.apc]
    g.connect_to_spine(apcs, sub_west, g.mv)

    # Distro + waste spines follow the same corridors; vents/scrubbers get L-runs.
    g.distro |= spine
    g.waste |= set(spine)
    g.l_route((-23, -3), (-9, -5), g.distro)       # canisters -> ring
    g.l_route((-23, -6), (-9, -5), g.waste)        # ring -> passive vent
    vent_tiles = {(x, y) for proto, x, y, _, _ in g.ents if proto == "GasVentPump"}
    scrub_tiles = {(x, y) for proto, x, y, _, _ in g.ents if proto == "GasVentScrubber"}
    g.distro -= vent_tiles
    g.waste -= scrub_tiles
    g.connect_to_spine(g.vents, (-23, -3), g.distro, forbidden=vent_tiles)
    g.connect_to_spine(g.scrubbers, (-24, -6), g.waste, forbidden=scrub_tiles)

    # LV along corridors too.
    for t in spine:
        g.lv.add(t)

    return g


PROTOS = set()


def load_protos():
    import glob, re
    for path in glob.glob("Resources/Prototypes/**/*.yml", recursive=True):
        txt = open(path, encoding="utf-8").read()
        for m in re.finditer(r"^- type: entity\n(?:(?!\n- ).)*?^  id: (\S+)", txt, re.M | re.S):
            PROTOS.add(m.group(1))


if __name__ == "__main__":
    load_protos()
    g = build()
    missing = sorted({p for p, *_ in g.ents if p not in PROTOS and not p.startswith("Gas") and not p.startswith("Windoor")})
    if missing:
        print("UNKNOWN PROTOTYPES:", missing)
    n = g.write("Resources/Maps/cloud.yml")
    print(f"wrote cloud.yml: {n} entities, {len(g.tiles)} tiles, {len(g.rooms)} rooms")
