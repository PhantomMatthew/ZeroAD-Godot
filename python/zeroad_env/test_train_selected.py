"""Sequential multi-select log-prob (joint over up to 8 slots)."""

import numpy as np
import pytest

from zeroad_env import layout as L
from zeroad_env.catalog import FN_MOVE, FN_TRAIN
from zeroad_env.train import (
    FANOUT_FNS,
    sequential_sample_selected,
    sequential_selected_logprob,
)


torch = pytest.importorskip("torch")


def test_fanout_fns_match_kernel() -> None:
    assert FN_MOVE in FANOUT_FNS
    assert 26 in FANOUT_FNS  # Formation uses every filled slot
    assert FN_TRAIN not in FANOUT_FNS


def test_sequential_sample_no_repeats_and_pads() -> None:
    torch.manual_seed(0)
    logits = torch.tensor([0.0, 4.0, 3.0, 2.0, 1.0], dtype=torch.float32)
    legal = np.array([True, True, True, True, True])
    selected = sequential_sample_selected(logits, legal, n_slots=3, torch_mod=torch)
    assert selected.shape == (L.MAX_SELECTED,)
    filled = [int(x) for x in selected if int(x) >= 0]
    assert len(filled) == 3
    assert len(set(filled)) == 3
    assert all(int(x) == -1 for x in selected[3:])


def test_sequential_logprob_sums_later_slots() -> None:
    logits = torch.tensor([0.0, 1.0, 2.0, 3.0], dtype=torch.float32, requires_grad=True)
    legal = np.array([True, True, True, True])
    one = np.array([3, -1, -1, -1, -1, -1, -1, -1], dtype=np.int32)
    two = np.array([3, 2, -1, -1, -1, -1, -1, -1], dtype=np.int32)
    lp1 = sequential_selected_logprob(logits, legal, one, torch_mod=torch)
    lp2 = sequential_selected_logprob(logits, legal, two, torch_mod=torch)
    assert float(lp2.detach()) < float(lp1.detach())
    lp2.backward()
    assert logits.grad is not None
    assert float(logits.grad.abs().sum()) > 0


def test_n_slots_one_only_fills_first() -> None:
    torch.manual_seed(1)
    logits = torch.zeros(6)
    legal = np.ones(6, dtype=bool)
    selected = sequential_sample_selected(logits, legal, n_slots=1, torch_mod=torch)
    assert int(selected[0]) >= 0
    assert all(int(x) == -1 for x in selected[1:])
