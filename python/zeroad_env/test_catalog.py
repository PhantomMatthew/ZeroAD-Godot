"""Catalog intern order and athen CC Train/Build/Research legal sets."""

import numpy as np

from zeroad_env.catalog import (
    CATALOG_FNS,
    CELL_FNS,
    FN_BUILD,
    FN_RESEARCH,
    FN_TRAIN,
    Catalog,
    legal_catalog_ids,
    merge_tokens,
)


def test_merge_tokens_parent_then_minus() -> None:
    merged = merge_tokens("units/{native}/support_civilian a", "units/{civ}/infantry_spearman_b -a")
    assert "units/{native}/support_civilian" in merged
    assert "units/{civ}/infantry_spearman_b" in merged
    assert merged.split().count("a") == 0


def test_cell_and_catalog_function_sets() -> None:
    assert 2 in CELL_FNS and 6 in CELL_FNS
    assert FN_TRAIN in CATALOG_FNS and FN_BUILD in CATALOG_FNS and FN_RESEARCH in CATALOG_FNS
    assert FN_TRAIN not in CELL_FNS


def test_athen_cc_train_build_research_interns() -> None:
    cat = Catalog.load()
    if len(cat.template_by_id) < 10:
        return
    cc = cat.id_of_template.get("structures/athen/civil_centre")
    assert cc is not None and cc > 0
    spear = cat.id_of_template.get("units/athen/infantry_spearman_b")
    civilian = cat.id_of_template.get("units/athen/support_civilian")
    villager = cat.id_of_template.get("units/athen/support_female_citizen")
    house = cat.id_of_template.get("structures/athen/house")
    phase = cat.id_of_tech.get("phase_town_athen") or cat.id_of_tech.get("phase_town")
    train = cat.legal_ids(cc, FN_TRAIN, "athen")
    assert spear in train
    assert civilian in train
    if villager:
        build = cat.legal_ids(villager, FN_BUILD, "athen")
        if house:
            assert house in build
    research = cat.legal_ids(cc, FN_RESEARCH, "athen")
    assert phase in research


def test_legal_catalog_ids_uses_selected_building() -> None:
    cat = Catalog.load()
    cc = cat.id_of_template.get("structures/athen/civil_centre")
    spear = cat.id_of_template.get("units/athen/infantry_spearman_b")
    if not cc or not spear:
        return
    ent = np.zeros((8, 13), dtype=np.int32)
    ent[0, 0] = 1
    ent[0, 1] = 1
    ent[0, 2] = spear
    ent[0, 8] = 2
    ent[1, 0] = 1
    ent[1, 1] = 1
    ent[1, 2] = cc
    ent[1, 8] = 1
    obs = {"entities": ent}
    ids = legal_catalog_ids(obs, FN_TRAIN, selected=1, catalog=cat)
    assert spear in ids
    assert cc not in ids


def test_legal_catalog_ids_prefers_obs_catalog() -> None:
    cat = Catalog.load()
    packed = np.zeros((8, 3, 16), dtype=np.int32)
    packed[1, 0, 0] = 42
    packed[1, 0, 1] = 7
    ent = np.zeros((8, 13), dtype=np.int32)
    ent[1, 0] = 1
    obs = {"entities": ent, "catalog": packed}
    ids = legal_catalog_ids(obs, FN_TRAIN, selected=1, catalog=cat)
    assert ids == [42, 7]
    empty = np.zeros((8, 3, 16), dtype=np.int32)
    ids_empty = legal_catalog_ids(
        {"entities": ent, "catalog": empty}, FN_TRAIN, selected=1, catalog=cat
    )
    assert ids_empty == []
