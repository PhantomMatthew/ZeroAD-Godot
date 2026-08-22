from __future__ import annotations

import shutil
import struct
import sys
import tempfile
import unittest
from pathlib import Path

TOOLS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS))

import fix_glb_skeleton_unit_space as unit_fix  # noqa: E402
from fix_glb_skeleton_unit_space import (  # noqa: E402
    process_goat_anim,
    read_glb,
    scale_accessor,
    track_max_key,
    write_glb,
)


class GoatAnimationScaleTests(unittest.TestCase):
    def test_goat_walk_uses_dae_local_matrix_translations(self) -> None:
        source = TOOLS.parent / "assets/animations/quadraped/goat_walk.glb"
        dae = (
            TOOLS.parent.parent
            / "binaries/data/mods/public/art/animation/quadraped/goat_walk.dae"
        )
        if not source.exists() or not dae.exists():
            self.skipTest("pipeline goat walk source assets are not present")
        self.assertTrue(
            hasattr(unit_fix, "repair_goat_translations_from_dae"),
            "DAE matrix translation repair is not implemented",
        )
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / source.name
            shutil.copy2(source, target)

            actions = unit_fix.repair_goat_translations_from_dae(target, dae, dry_run=False)

            self.assertTrue(any(action.startswith("dae-position:shoulder_front_r") for action in actions))
            gltf, rest = read_glb(target)
            bin_ = memoryview(rest)[8:]
            first = None
            for animation in gltf.get("animations", []):
                for channel in animation.get("channels", []):
                    target_info = channel.get("target", {})
                    node = gltf["nodes"][target_info["node"]]
                    if target_info.get("path") != "translation":
                        continue
                    if node.get("name") != "shoulder_front_r":
                        continue
                    sampler = animation["samplers"][channel["sampler"]]
                    view, ncomp, _count, _stride = unit_fix.accessor_view(
                        gltf, bin_, sampler["output"]
                    )
                    self.assertEqual(ncomp, 3)
                    first = struct.unpack_from("<3f", view, 0)
            self.assertIsNotNone(first)
            self.assertAlmostEqual(first[0], 0.2851965, places=5)
            self.assertAlmostEqual(first[1], 2.507166, places=5)
            self.assertAlmostEqual(first[2], 0.3374774, places=5)

    def test_base_scale_is_canonicalized_after_partial_prior_fix(self) -> None:
        source = TOOLS.parent / "assets/animations/quadraped/goat_idle_01.glb"
        if not source.exists():
            self.skipTest(f"pipeline asset not present: {source}")
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / source.name
            shutil.copy2(source, target)
            gltf, rest = read_glb(target)
            bin_ = memoryview(bytearray(rest))[8:]
            mutable_rest = bin_.obj
            for animation in gltf.get("animations", []):
                for channel in animation.get("channels", []):
                    target_info = channel.get("target", {})
                    if target_info.get("path") != "scale":
                        continue
                    if gltf["nodes"][target_info["node"]].get("name") != "Base":
                        continue
                    sampler = animation["samplers"][channel["sampler"]]
                    scale_accessor(gltf, bin_, sampler["output"], 440.474)
            write_glb(target, gltf, bytes(mutable_rest))

            actions, warnings = process_goat_anim(target, dry_run=False)

            self.assertEqual(warnings, [])
            self.assertTrue(any(action.startswith("goat-scale:Base") for action in actions))
            gltf, rest = read_glb(target)
            bin_ = memoryview(rest)[8:]
            base_scale_max = 0.0
            for animation in gltf.get("animations", []):
                for channel in animation.get("channels", []):
                    target_info = channel.get("target", {})
                    if target_info.get("path") != "scale":
                        continue
                    if gltf["nodes"][target_info["node"]].get("name") != "Base":
                        continue
                    sampler = animation["samplers"][channel["sampler"]]
                    base_scale_max = max(base_scale_max, track_max_key(gltf, bin_, sampler))

            # track_max_key returns Euclidean magnitude; identity [1,1,1] is sqrt(3).
            self.assertAlmostEqual(base_scale_max, 3**0.5, places=4)


if __name__ == "__main__":
    unittest.main()
