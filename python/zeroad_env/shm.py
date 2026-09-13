"""Gymnasium-style env that talks to ZeroAD.RlHost over a file-backed mmap."""

from __future__ import annotations

import contextlib
import mmap
import os
import struct
import subprocess
import tempfile
import time
from pathlib import Path
from typing import Any

import numpy as np

from zeroad_env import layout as L
from zeroad_env.host import host_command


_I32 = struct.Struct("<i")
_U32 = struct.Struct("<I")


class ZeroADShmEnv:
    """Vector of ``n_slots`` headless matches. Observations are int32 numpy arrays."""

    def __init__(
        self,
        n_slots: int = 1,
        *,
        seed: int = 1,
        privileged: bool = False,
        step_mul: int = 1,
        shm_path: str | None = None,
    ) -> None:
        self.n_slots = max(1, n_slots)
        self._seed = seed
        path = Path(shm_path) if shm_path else Path(
            tempfile.gettempdir(), f"zeroad_rl_{os.getpid()}.shm"
        )
        self._path = path
        cmd = [
            *host_command(),
            "--shm",
            str(path),
            "--slots",
            str(self.n_slots),
            "--seed",
            str(seed),
            "--step-mul",
            str(step_mul),
        ]
        if privileged:
            cmd.append("--privileged")
        self._proc = subprocess.Popen(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )
        assert self._proc.stdout is not None
        deadline = time.time() + 60
        ready = ""
        while time.time() < deadline:
            line = self._proc.stdout.readline()
            if not line:
                err = self._proc.stderr.read() if self._proc.stderr else ""
                msg = f"RlHost exited before READY: {err}"
                raise RuntimeError(msg)
            if line.startswith("READY"):
                ready = line
                break
        if not ready:
            self.close()
            msg = "RlHost did not print READY"
            raise TimeoutError(msg)
        size = L.file_bytes(self.n_slots)
        self._fd = os.open(str(path), os.O_RDWR)
        self._mm = mmap.mmap(self._fd, size)
        magic = _U32.unpack_from(self._mm, L.OFF_MAGIC)[0]
        version = _I32.unpack_from(self._mm, L.OFF_VERSION)[0]
        if magic != L.MAGIC or version != L.VERSION:
            self.close()
            msg = f"layout mismatch magic={magic:#x} version={version}"
            raise RuntimeError(msg)

    def reset(self) -> dict[str, np.ndarray]:
        """Reset every slot and return batched observations."""
        self._issue(L.CMD_RESET)
        return self._read_obs()

    def step(self, actions: dict[str, np.ndarray] | None = None) -> tuple[
        dict[str, np.ndarray], np.ndarray, np.ndarray, list[dict[str, Any]]
    ]:
        """Step all slots. Action dict keys match the frozen action struct."""
        if actions is None:
            actions = {
                "function": np.zeros(self.n_slots, dtype=np.int32),
                "selected": np.full(self.n_slots, -1, dtype=np.int32),
                "target": np.full(self.n_slots, -1, dtype=np.int32),
                "cell_x": np.zeros(self.n_slots, dtype=np.int32),
                "cell_z": np.zeros(self.n_slots, dtype=np.int32),
                "catalog": np.zeros(self.n_slots, dtype=np.int32),
            }
        for s in range(self.n_slots):
            base = L.slot_offset(s) + L.OFF_ACTION
            _I32.pack_into(self._mm, base + L.ACT_FUNCTION, int(actions["function"][s]))
            _I32.pack_into(self._mm, base + L.ACT_SELECTED, int(actions["selected"][s]))
            _I32.pack_into(self._mm, base + L.ACT_TARGET, int(actions["target"][s]))
            _I32.pack_into(self._mm, base + L.ACT_CELL_X, int(actions["cell_x"][s]))
            _I32.pack_into(self._mm, base + L.ACT_CELL_Z, int(actions["cell_z"][s]))
            _I32.pack_into(self._mm, base + L.ACT_CATALOG, int(actions["catalog"][s]))
        self._issue(L.CMD_STEP)
        obs = self._read_obs()
        reward = np.empty(self.n_slots, dtype=np.int32)
        done = np.empty(self.n_slots, dtype=np.bool_)
        infos: list[dict[str, Any]] = []
        for s in range(self.n_slots):
            off = L.slot_offset(s)
            reward[s] = _I32.unpack_from(self._mm, off + L.OFF_REWARD)[0]
            done[s] = _I32.unpack_from(self._mm, off + L.OFF_DONE)[0] != 0
            turn = _I32.unpack_from(self._mm, off + L.OFF_TURN)[0]
            infos.append({"turn": turn})
        return obs, reward, done, infos

    def close(self) -> None:
        """Stop the host and unmap the file."""
        try:
            if getattr(self, "_mm", None) is not None:
                _I32.pack_into(self._mm, L.OFF_CMD, L.CMD_QUIT)
                seq = _I32.unpack_from(self._mm, L.OFF_SEQ_IN)[0] + 1
                _I32.pack_into(self._mm, L.OFF_SEQ_IN, seq)
                self._mm.flush()
                self._mm.close()
        except (BufferError, ValueError, OSError):
            pass
        proc = getattr(self, "_proc", None)
        if proc is not None and proc.poll() is None:
            try:
                proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                proc.kill()
        fd = getattr(self, "_fd", None)
        if fd is not None:
            os.close(fd)
        path = getattr(self, "_path", None)
        if path is not None:
            with contextlib.suppress(OSError):
                path.unlink()

    def _issue(self, cmd: int) -> None:
        _I32.pack_into(self._mm, L.OFF_CMD, cmd)
        seq = _I32.unpack_from(self._mm, L.OFF_SEQ_IN)[0] + 1
        _I32.pack_into(self._mm, L.OFF_SEQ_IN, seq)
        self._mm.flush()
        deadline = time.time() + 120
        while time.time() < deadline:
            out = _I32.unpack_from(self._mm, L.OFF_SEQ_OUT)[0]
            if out == seq:
                return
            time.sleep(0.001)
        msg = "RlHost step timed out"
        raise TimeoutError(msg)

    def _read_obs(self) -> dict[str, np.ndarray]:
        entities = np.empty((self.n_slots, L.MAX_ENTITIES, L.ENTITY_FEAT), dtype=np.int32)
        spatial = np.empty(
            (self.n_slots, L.SPATIAL_CHANNELS, L.SPATIAL_SIZE, L.SPATIAL_SIZE),
            dtype=np.int32,
        )
        scalars = np.empty((self.n_slots, L.SCALAR_COUNT), dtype=np.int32)
        mask = np.empty((self.n_slots, L.MASK_BYTES), dtype=np.uint8)
        for s in range(self.n_slots):
            off = L.slot_offset(s)
            entities[s] = np.frombuffer(
                self._mm,
                dtype="<i4",
                count=L.MAX_ENTITIES * L.ENTITY_FEAT,
                offset=off + L.OFF_ENTITIES,
            ).reshape(L.MAX_ENTITIES, L.ENTITY_FEAT)
            spatial[s] = np.frombuffer(
                self._mm,
                dtype="<i4",
                count=L.SPATIAL_CHANNELS * L.SPATIAL_SIZE * L.SPATIAL_SIZE,
                offset=off + L.OFF_SPATIAL,
            ).reshape(L.SPATIAL_CHANNELS, L.SPATIAL_SIZE, L.SPATIAL_SIZE)
            scalars[s] = np.frombuffer(
                self._mm,
                dtype="<i4",
                count=L.SCALAR_COUNT,
                offset=off + L.OFF_SCALARS,
            )
            mask[s] = np.frombuffer(
                self._mm,
                dtype=np.uint8,
                count=L.MASK_BYTES,
                offset=off + L.OFF_MASK,
            )
        return {
            "entities": entities.copy(),
            "spatial": spatial.copy(),
            "scalars": scalars.copy(),
            "function_mask": mask.copy(),
        }
