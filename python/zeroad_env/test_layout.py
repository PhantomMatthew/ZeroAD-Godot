"""Layout constants stay in lockstep with src/ZeroAD.Sim/RL/ShmLayout.cs."""

from zeroad_env import layout as L


def test_layout_matches_csharp_p0() -> None:
    """Spot-check frozen sizes from the C# ShmLayout tests."""
    assert L.MAGIC == 0x5A414452
    assert L.VERSION == 2
    assert L.HEADER_BYTES == 64
    assert L.ENTITIES_BYTES == 512 * 13 * 4
    assert L.SPATIAL_BYTES == 5 * 64 * 64 * 4
    assert L.SCALARS_BYTES == 11 * 4
    assert L.MASK_BYTES == 32
    assert L.ENTITY_MASK_BYTES == 512 * 4
    assert L.ACTION_BYTES == 128
    assert L.MAX_SELECTED == 8
    assert L.N_FUNCTIONS == 32
    assert L.OFF_ENTITY_MASK == L.OFF_MASK + L.MASK_BYTES
    assert L.OFF_ACTION == L.OFF_ENTITY_MASK + L.ENTITY_MASK_BYTES
    assert L.SLOT_BYTES == L.OFF_ACTION + 128
    assert L.ACT_OPP_BASE == 64
    assert L.file_bytes(2) == L.HEADER_BYTES + 2 * L.SLOT_BYTES
