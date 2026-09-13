"""gRPC Session smoke: spawn host, reset, noop step, close."""

from zeroad_env.gym_env import make_env


def test_grpc_session_reset_and_step() -> None:
    """Requires a built ZeroAD.RlHost and grpcio."""
    env = make_env(backend="grpc", seed=3, privileged=True)
    try:
        obs, _info = env.reset()
        assert obs["entities"].shape == (512, 13)
        assert obs["spatial"].shape == (4, 64, 64)
        obs2, reward, terminated, truncated, info = env.step({"function": 0})
        assert obs2["entities"].shape == (512, 13)
        assert reward == 0.0
        assert terminated is False
        assert truncated is False
        assert "turn" in info
    finally:
        env.close()
