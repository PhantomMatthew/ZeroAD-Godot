"""Intern maps matching ``RlCatalog.FromNames`` plus Train/Build/Research legal ids."""

from __future__ import annotations

import json
import os
from dataclasses import dataclass
from pathlib import Path
from xml.etree import ElementTree as ET

from zeroad_env import layout as L
from zeroad_env.host import find_repo_root


FN_BUILD = 6
FN_TRAIN = 7
FN_RESEARCH = 8
FN_MOVE = 2
FN_PATROL = 10
FN_ATTACK_WALK = 11
FN_RALLY = 14
FN_FORMATION = 26
FN_TRIBUTE = 27
FN_BARTER = 28
FN_TRADE_GOODS = 31

CELL_FNS = frozenset({FN_MOVE, FN_BUILD, FN_PATROL, FN_ATTACK_WALK, FN_RALLY})
CATALOG_FNS = frozenset({FN_BUILD, FN_TRAIN, FN_RESEARCH})
PACKED_CATALOG = {
    FN_TRIBUTE: list(range(12)),
    FN_BARTER: list(range(32)),
    FN_TRADE_GOODS: list(range(256)),
    FN_FORMATION: list(range(30)),
}
KIND_BY_FN = {
    FN_TRAIN: L.CATALOG_KIND_TRAIN,
    FN_BUILD: L.CATALOG_KIND_BUILD,
    FN_RESEARCH: L.CATALOG_KIND_RESEARCH,
}

OWN = 1
FLAG_BUILDING = 1


def merge_tokens(existing: str, overlay: str) -> str:
    """ParamNode.MergeTokens: parent tokens, then overlay; ``-tok`` deletes."""
    tokens: list[str] = []
    seen: set[str] = set()
    for t in existing.split():
        if t not in seen:
            tokens.append(t)
            seen.add(t)
    for tok in overlay.split():
        if tok.startswith("-") and len(tok) > 1:
            drop = tok[1:]
            if drop in seen:
                tokens = [x for x in tokens if x != drop]
                seen.remove(drop)
        elif tok not in seen:
            tokens.append(tok)
            seen.add(tok)
    return " ".join(tokens)


def find_mods_public(explicit: str | None = None) -> Path | None:
    """Same search order as ``RlDataRoot.FindModsPublic``."""
    env = explicit or os.environ.get("ZEROAD_DATA")
    hit = _try_mods_public(env)
    if hit is not None:
        return hit
    try:
        repo = find_repo_root()
    except FileNotFoundError:
        repo = Path(__file__).resolve()
    for parent in [repo, *Path(__file__).resolve().parents]:
        staged = parent / "godot" / "export" / "data" / "mods" / "public"
        if (staged / "simulation" / "templates").is_dir():
            return staged
    return None


def _try_mods_public(root: str | None) -> Path | None:
    if not root:
        return None
    path = Path(root)
    if (path / "simulation" / "templates").is_dir():
        return path
    nested = path / "mods" / "public"
    if (nested / "simulation" / "templates").is_dir():
        return nested
    export = path / "godot" / "export" / "data" / "mods" / "public"
    if (export / "simulation" / "templates").is_dir():
        return export
    return None


@dataclass
class _Bits:
    trainer: str = ""
    builder: str = ""
    researcher: str = ""
    native_civ: str = ""


class Catalog:
    """Sorted intern ids (1-based) plus merged Trainer/Builder/Researcher tokens."""

    def __init__(self, mods_public: Path | None) -> None:
        self.template_by_id: list[str] = [""]
        self.id_of_template: dict[str, int] = {}
        self.tech_by_id: list[str] = [""]
        self.id_of_tech: dict[str, int] = {}
        self._bits: dict[str, _Bits] = {}
        self._templates_root: Path | None = None
        if mods_public is None:
            return
        root = mods_public / "simulation" / "templates"
        if root.is_dir():
            self._templates_root = root
            names = sorted(
                (
                    p.relative_to(root).as_posix()[: -len(".xml")]
                    for p in root.rglob("*.xml")
                ),
                key=lambda n: n,
            )
            for n in names:
                i = len(self.template_by_id)
                self.template_by_id.append(n)
                self.id_of_template[n] = i
        tech_dir = mods_public / "simulation" / "data" / "technologies"
        tech_names: list[str] = []
        if tech_dir.is_dir():
            for f in tech_dir.rglob("*.json"):
                try:
                    data = json.loads(f.read_text(encoding="utf-8"))
                except (OSError, json.JSONDecodeError):
                    continue
                if isinstance(data, dict) and "pair" in data:
                    continue
                tech_names.append(f.stem)
        for n in sorted(set(tech_names), key=lambda x: x):
            i = len(self.tech_by_id)
            self.tech_by_id.append(n)
            self.id_of_tech[n] = i

    @classmethod
    def load(cls, explicit: str | None = None) -> Catalog:
        return cls(find_mods_public(explicit))

    def template_name(self, intern: int) -> str:
        if intern <= 0 or intern >= len(self.template_by_id):
            return ""
        return self.template_by_id[intern]

    def legal_ids(self, template_intern: int, fn: int, civ: str) -> list[int]:
        """Intern catalog ids legal for Train/Build/Research on this template."""
        name = self.template_name(template_intern)
        if not name:
            return []
        bits = self._resolve(name)
        owner = civ if civ else bits.native_civ
        if fn == FN_TRAIN:
            return self._intern_templates(_expand(bits.trainer, owner, bits.native_civ))
        if fn == FN_BUILD:
            return self._intern_templates(_expand(bits.builder, owner, bits.native_civ))
        if fn == FN_RESEARCH:
            return self._intern_techs(_expand(bits.researcher, owner, bits.native_civ))
        return []

    def _intern_templates(self, names: list[str]) -> list[int]:
        out: list[int] = []
        for n in names:
            i = self.id_of_template.get(n)
            if i:
                out.append(i)
        return out

    def _intern_techs(self, names: list[str]) -> list[int]:
        out: list[int] = []
        for n in names:
            i = self.id_of_tech.get(n)
            if i:
                out.append(i)
        return out

    def _resolve(self, name: str) -> _Bits:
        cached = self._bits.get(name)
        if cached is not None:
            return cached
        bits = _Bits()
        self._resolve_into(bits, name, set())
        self._bits[name] = bits
        return bits

    def _resolve_into(self, bits: _Bits, name: str, stack: set[str]) -> None:
        pipe = name.find("|")
        if pipe >= 0:
            self._resolve_into(bits, name[pipe + 1 :].strip(), stack)
            self._resolve_into(bits, name[:pipe].strip(), stack)
            return
        if not name or name in stack:
            return
        stack.add(name)
        root = self._read_xml(name)
        if root is None:
            stack.discard(name)
            return
        parent = root.get("parent")
        if parent:
            self._resolve_into(bits, parent, stack)
        _apply_xml(bits, root)
        stack.discard(name)

    def _read_xml(self, name: str) -> ET.Element | None:
        if self._templates_root is None:
            return None
        rel = name.replace("\\", "/") + ".xml"
        for folder in ("special/filter", "mixins", ""):
            path = self._templates_root / folder / rel if folder else self._templates_root / rel
            if path.is_file():
                try:
                    return ET.parse(path).getroot()
                except ET.ParseError:
                    return None
        return None


def _expand(tokens: str, civ: str, native: str) -> list[str]:
    out: list[str] = []
    for raw in tokens.split():
        tok = raw
        if native:
            tok = tok.replace("{native}", native)
        if civ:
            tok = tok.replace("{civ}", civ)
        if "{" in tok:
            continue
        if tok not in out:
            out.append(tok)
    return out


def _el_text(parent: ET.Element, path: str) -> str | None:
    el = parent.find(path)
    if el is None:
        return None
    text = "".join(el.itertext())
    return text


def _apply_xml(bits: _Bits, root: ET.Element) -> None:
    trainer = _el_text(root, "Trainer/Entities")
    if trainer is not None:
        bits.trainer = merge_tokens(bits.trainer, trainer)
    builder = _el_text(root, "Builder/Entities")
    if builder is not None:
        bits.builder = merge_tokens(bits.builder, builder)
    researcher = _el_text(root, "Researcher/Technologies")
    if researcher is not None:
        bits.researcher = merge_tokens(bits.researcher, researcher)
    civ = _el_text(root, "Identity/Civ")
    if civ is not None and civ.strip():
        bits.native_civ = civ.strip()


def infer_civ(entities, catalog: Catalog) -> str:
    """Owner civ from an own building template path ``structures/{civ}/...``."""
    for i in range(entities.shape[0]):
        if entities[i, 0] == 0 or int(entities[i, 1]) != OWN:
            continue
        if (int(entities[i, 8]) & FLAG_BUILDING) == 0:
            continue
        name = catalog.template_name(int(entities[i, 2]))
        parts = name.split("/")
        if len(parts) >= 2 and parts[0] == "structures":
            return parts[1]
    return "athen"


def field_unit_template_ids(entities) -> list[int]:
    """Own non-building template intern ids already on the field (Train fallback)."""
    seen: set[int] = set()
    out: list[int] = []
    for i in range(entities.shape[0]):
        if entities[i, 0] == 0 or int(entities[i, 1]) != OWN:
            continue
        if (int(entities[i, 8]) & FLAG_BUILDING) != 0:
            continue
        tid = int(entities[i, 2])
        if tid > 0 and tid not in seen:
            seen.add(tid)
            out.append(tid)
    return out


def obs_catalog_ids(obs: dict, fn: int, selected: int) -> list[int] | None:
    """Sim-packed intern ids when ``obs['catalog']`` is present; None if absent."""
    packed = obs.get("catalog")
    if packed is None:
        return None
    kind = KIND_BY_FN.get(fn)
    if kind is None:
        return []
    arr = packed
    if selected < 0 or selected >= arr.shape[0]:
        return []
    row = arr[selected, kind]
    return [int(x) for x in row.tolist() if int(x) > 0]


def legal_catalog_ids(
    obs: dict,
    fn: int,
    selected: int,
    catalog: Catalog,
) -> list[int]:
    """Legal intern ids for the sampled function + selected entity.

    Prefers the sim-resolved per-entity catalog tensor when present (layout v3).
    Falls back to XML intern maps only when that tensor is missing (v2 hosts / unit tests).
    """
    if fn not in CATALOG_FNS:
        return []
    packed = obs_catalog_ids(obs, fn, selected)
    if packed is not None:
        return packed
    ent = obs["entities"]
    if selected < 0 or selected >= ent.shape[0] or ent[selected, 0] == 0:
        return []
    tmpl = int(ent[selected, 2])
    civ = infer_civ(ent, catalog)
    ids = catalog.legal_ids(tmpl, fn, civ)
    if ids:
        return ids
    if fn == FN_TRAIN:
        return field_unit_template_ids(ent)
    return []
