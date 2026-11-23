"""Bounded validation for content accepted by the SGL library."""
import hashlib
import io
import json
import re
import zipfile

MAX_MAP = 128_000_000
MAX_MODE = 1_048_576
VARIANTS = {
    "GV_KOTH_Team_Default", "GV_Deathmatch_Team_SWAT_Aeros",
    "GV_Deathmatch_Team_1round_Default", "GV_DOM_Team_1round_Default",
    "GV_Deathmatch_Team_1v1", "GV_Deathmatch_Team_BigHeadSnipers",
    "GV_Splitball_Team_Default",
}


class Problem(Exception):
    def __init__(self, status, message):
        super().__init__(message)
        self.status = status


def require(test, message, status=400):
    if not test:
        raise Problem(status, message)


def text(value, name, maximum, empty=False):
    require(isinstance(value, str), f"{name} must be text.")
    value = value.strip()
    multiline = name in ("description", "change note", "creator credits")
    require((empty or value) and len(value) <= maximum and not any(ord(c) < 32 and not (multiline and c in "\r\n\t") for c in value), f"Invalid {name}.")
    return value


def identity(value):
    require(isinstance(value, str) and re.fullmatch(r"[a-f0-9]{32}", value), "Invalid item or revision identity.")
    return value


def digest(data):
    return hashlib.sha256(data).hexdigest()


def json_bytes(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), allow_nan=False).encode("utf-8")


def mode(payload):
    require(isinstance(payload, dict) and set(payload) == {"Version", "Variant", "Options"}, "Invalid gamemode payload.")
    require(type(payload["Version"]) is int and payload["Version"] == 1 and isinstance(payload["Variant"], str) and payload["Variant"] in {"PortalWarsGameVariant:" + v for v in VARIANTS}, "Unsupported gamemode base.")
    options = payload["Options"]
    require(isinstance(options, dict) and set(options) == {"CustomName", "OptionMap"}, "Invalid gamemode options.")
    text(options["CustomName"], "gamemode name", 80)
    contexts = options["OptionMap"]
    require(isinstance(contexts, dict) and 1 <= len(contexts) <= 32, "Invalid option contexts.")
    for key, context in contexts.items():
        require(re.fullmatch(r"[a-fA-F0-9]{32}", key) and isinstance(context, dict) and set(context) <= {"Options", "InheritsFrom"}, "Invalid option context.")
        require("InheritsFrom" not in context or context["InheritsFrom"] in contexts, "Invalid inherited context.")
        values = context.get("Options")
        require(isinstance(values, dict) and len(values) <= 128 and all(k.startswith("/Script/") for k in values), "Invalid option types.")
    data = json_bytes(payload)
    require(len(data) <= MAX_MODE, "Gamemode exceeds 1 MB.")
    return data


def map_archive(data):
    require(4 <= len(data) <= MAX_MAP and data[:2] == b"PK", "Expected a map .bin ZIP archive.")
    try:
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            entries = archive.infolist()
            require(len(entries) <= 1024 and sum(e.file_size for e in entries) <= 1_000_000_000, "Map expansion limit exceeded.")
            names = set()
            for entry in entries:
                parts = entry.filename.split("/")
                require(not entry.filename.startswith("/") and "\\" not in entry.filename and not any(p in (".", "..") or ":" in p for p in parts), "Unsafe map entry.")
                require(entry.filename.lower() not in names, "Duplicate map entry.")
                names.add(entry.filename.lower())
            require("World.cf1047" in archive.namelist() and archive.getinfo("World.cf1047").file_size > 0, "Archive contains no map data. Prefabs are not supported.")
            # Stream the world to check decompression and CRC without retaining it.
            with archive.open("World.cf1047") as world:
                while world.read(65_536):
                    pass
            metadata = {}
            if "info.json" in archive.namelist():
                require(archive.getinfo("info.json").file_size <= 65_536, "Map metadata exceeds 64 KB.")
                metadata = json.loads(archive.read("info.json").decode("utf-8-sig"))
                require(isinstance(metadata, dict), "Invalid map metadata.")
            preview = None
            if "Screenshot.jpg" in archive.namelist() and archive.getinfo("Screenshot.jpg").file_size <= 2_000_000:
                image = archive.read("Screenshot.jpg")
                if image.startswith(b"\xff\xd8\xff"):
                    preview = image
            return metadata, preview
    except (zipfile.BadZipFile, UnicodeError, json.JSONDecodeError, RuntimeError, EOFError, NotImplementedError) as error:
        raise Problem(400, "Invalid or damaged map archive.") from error
