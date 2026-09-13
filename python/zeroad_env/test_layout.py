"""Layout constants stay in lockstep with src/ZeroAD.Sim/RL/ShmLayout.cs."""

from zeroad_env import layout as L


def test_layout_matches_csharp_p0() -> None:
    """Spot-check frozen sizes from the C# ShmLayout tests."""
    assert L.MAGIC == 0x5A414452
    assert L.VERSION == 1
    assert L.HEADER_BYTES == 64
    assert L.ENTITIES_BYTES == 512 * 13 * 4
    assert L.SPATIAL_BYTES == 4 * 64 * 64 * 4
    assert L.SCALARS_BYTES == 11 * 4
    assert L.SLOT_BYTES == L.OFF_ACTION + 32
    assert L.file_bytes(2) == L.HEADER_BYTES + 2 * L.SLOT_BYTES
