"""gRPC client for ZeroAD.RlHost --grpc (remote / debug path, not the training default)."""

from __future__ import annotations

import contextlib
import queue
import re
import subprocess
import time
from collections.abc import Iterator
from typing import Any
from urllib.parse import urlparse

import grpc
import numpy as np

from zeroad_env import layout as L
from zeroad_env import zeroad_rl_pb2 as pb2
from zeroad_env import zeroad_rl_pb2_grpc as pb2_grpc
from zeroad_env.host import host_command


def _obs_to_numpy(obs: pb2.Observation) -> dict[str, np.ndarray]:
    entities = np.array(obs.entities, dtype=np.int32).reshape(L.MAX_ENTITIES, L.ENTITY_FEAT)
    spatial = np.array(obs.spatial, dtype=np.int32).reshape(
        L.SPATIAL_CHANNELS, L.SPATIAL_SIZE, L.SPATIAL_SIZE
    )
    scalars = np.array(obs.scalars, dtype=np.int32)
    mask = np.frombuffer(obs.function_mask, dtype=np.uint8)
    if mask.size < L.MASK_BYTES:
        mask = np.pad(mask, (0, L.MASK_BYTES - mask.size))
    return {
        "entities": entities,
        "spatial": spatial,
        "scalars": scalars,
        "function_mask": mask[: L.MASK_BYTES],
    }


def _action_msg(action: dict[str, int] | None) -> pb2.Action:
    action = action or {}
    return pb2.Action(
        function=int(action.get("function", 0)),
        selected_index=int(action.get("selected", -1)),
        target_entity_index=int(action.get("target", -1)),
        target_cell_x=int(action.get("cell_x", 0)),
        target_cell_z=int(action.get("cell_z", 0)),
        catalog_id=int(action.get("catalog", 0)),
    )


class ZeroADGrpcEnv:
    """Single-match env over the Session bidi stream.

    Spawns the host unless ``address`` is set.
    """

    def __init__(
        self,
        *,
        seed: int = 1,
        privileged: bool = False,
        step_mul: int = 1,
        command_delay: int = 2,
        address: str | None = None,
    ) -> None:
        self._seed = seed
        self._privileged = privileged
        self._step_mul = step_mul
        self._command_delay = command_delay
        self._proc: subprocess.Popen[str] | None = None
        self._out: queue.Queue[pb2.ClientMessage | None] = queue.Queue()
        self._last: pb2.Observation | None = None
        target = address or self._spawn_host()
        self._channel = grpc.insecure_channel(target)
        stub = pb2_grpc.ZeroAdRlStub(self._channel)
        self._responses = stub.Session(self._requests())

    def _spawn_host(self) -> str:
        cmd = [*host_command(), "--grpc", "0"]
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
                ready = line.strip()
                break
        if not ready:
            self.close()
            msg = "RlHost did not print READY"
            raise TimeoutError(msg)
        match = re.search(r"grpc=(\S+)", ready)
        if not match:
            self.close()
            msg = f"unparseable READY line: {ready}"
            raise RuntimeError(msg)
        parsed = urlparse(match.group(1))
        host = parsed.hostname or "127.0.0.1"
        port = parsed.port
        if port is None:
            self.close()
            msg = f"READY grpc url missing port: {ready}"
            raise RuntimeError(msg)
        return f"{host}:{port}"

    def _requests(self) -> Iterator[pb2.ClientMessage]:
        while True:
            item = self._out.get()
            if item is None:
                return
            yield item

    def _rpc(self, msg: pb2.ClientMessage) -> pb2.Observation:
        self._out.put(msg)
        obs = next(self._responses)
        self._last = obs
        return obs

    def reset(self, seed: int | None = None) -> dict[str, np.ndarray]:
        """Send Reset on the session stream."""
        req = pb2.ResetRequest(
            seed=int(seed if seed is not None else self._seed),
            privileged=self._privileged,
            step_mul=self._step_mul,
            command_delay=self._command_delay,
        )
        return _obs_to_numpy(self._rpc(pb2.ClientMessage(reset=req)))

    def step(
        self, action: dict[str, int] | None = None
    ) -> tuple[dict[str, np.ndarray], float, bool, dict[str, Any]]:
        """Send Step on the session stream."""
        obs_msg = self._rpc(pb2.ClientMessage(step=_action_msg(action)))
        obs = _obs_to_numpy(obs_msg)
        info = {"turn": int(obs_msg.turn)}
        return obs, float(obs_msg.reward), bool(obs_msg.done), info

    def close(self) -> None:
        """End the stream and stop a spawned host."""
        with contextlib.suppress(ValueError, OSError):
            self._out.put(None)
        channel = getattr(self, "_channel", None)
        if channel is not None:
            channel.close()
        proc = self._proc
        if proc is not None and proc.poll() is None:
            proc.terminate()
            try:
                proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                proc.kill()
        self._proc = None
