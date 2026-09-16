"""Catalog packing ids and curriculum schedule."""

from zeroad_env.catalog import FN_BARTER, FN_FORMATION, FN_TRIBUTE, PACKED_CATALOG
from zeroad_env.train import _curriculum_turns


def test_packed_catalog_sizes() -> None:
    assert PACKED_CATALOG[FN_TRIBUTE][-1] == 11
    assert PACKED_CATALOG[FN_BARTER][-1] == 31
    assert 10 in PACKED_CATALOG[FN_FORMATION]
    assert 20 in PACKED_CATALOG[FN_FORMATION]


def test_curriculum_ramps() -> None:
    assert _curriculum_turns(0, 8, 256) == 256
    assert _curriculum_turns(7, 8, 256) == 2048
    assert _curriculum_turns(0, 8, 1024) == 1024
