"""
Make the 3v3 base model from the trained 1v1 self-play model.

The 1v1 network takes 21 observations; the 3v3 agent sends 46 (the same 21, then 25 about the
other players). This script copies the trained network into a 46-input network:

  * the weights for the 21 original inputs are copied exactly,
  * the weights for the 25 new inputs start at ZERO, so at step 0 the new network ignores the
    other players and plays exactly like the 1v1 model; training then learns to use them,
  * every other layer (hidden layer, action heads, value head) is copied unchanged,
  * the observation normaliser gets starting statistics for the new inputs (borrowed from the
    agent's own position / velocity / possession statistics), and its sample count is capped so
    the statistics can adapt to 3v3 instead of being frozen by the millions of 1v1 samples,
  * the Adam optimiser state is padded the same way.

It then builds the real ML-Agents networks, feeds both the same observations and checks that the
outputs are identical.

Usage (from the repo root, venv active):
    python tools/convert_to_3v3.py
Writes:
    results/3v3_base/FootballAgent/checkpoint.pt        (used by --initialize-from=3v3_base)
    saved_models/3v3_base/FootballAgent/checkpoint.pt   (backup that can be committed)
"""
import argparse
import os
import shutil

import torch

OLD_OBS = 21
NEW_OBS = 46
PLAYER_SLOTS = 5          # 2 teammates + 3 opponents
PER_PLAYER = 5            # rel pos x, rel pos z, vel x, vel z, has-ball
NORM_STEPS_CAP = 100_000  # how much weight the old statistics keep against new 3v3 samples

# Index of the original observation whose statistics seed each new per-player observation
# (the mean is set to 0 for positions and velocities: other players are spread around you).
#   rel pos x <- own pos.x (0), rel pos z <- own pos.z (1), vel x <- own vel.x (2),
#   vel z <- own vel.z (3), has-ball <- own hasPossession (13, mean kept)
SEED_FROM = [0, 1, 2, 3, 13]
KEEP_MEAN = [False, False, False, False, True]

NORM_PREFIXES = [
    "network_body.observation_encoder.processors.0.normalizer.",
    "network_body.processors.0.normalizer.",
]
FIRST_LAYER = "network_body._body_endoder.seq_layers.0.weight"


def expand_normalizer(sd, prefix):
    steps = sd[prefix + "normalization_steps"].clone()
    mean = sd[prefix + "running_mean"].clone()
    var_sum = sd[prefix + "running_variance"].clone()   # sum of squares; variance = var_sum / steps
    variance = var_sum / steps

    new_mean, new_var = [], []
    for _ in range(PLAYER_SLOTS):
        for k in range(PER_PLAYER):
            src = SEED_FROM[k]
            new_mean.append(mean[src] if KEEP_MEAN[k] else torch.tensor(0.0))
            new_var.append(variance[src])
    mean = torch.cat([mean, torch.stack(new_mean)])
    variance = torch.cat([variance, torch.stack(new_var)])

    capped = torch.minimum(steps, torch.tensor(NORM_STEPS_CAP, dtype=steps.dtype))
    sd[prefix + "normalization_steps"] = capped
    sd[prefix + "running_mean"] = mean
    sd[prefix + "running_variance"] = variance * capped.to(variance.dtype)


def expand_network(sd):
    for prefix in NORM_PREFIXES:
        expand_normalizer(sd, prefix)
    w = sd[FIRST_LAYER]
    assert w.shape[1] == OLD_OBS, f"expected {OLD_OBS} inputs, found {w.shape[1]}"
    sd[FIRST_LAYER] = torch.cat([w, torch.zeros(w.shape[0], NEW_OBS - OLD_OBS, dtype=w.dtype)], dim=1)


def expand_optimizer(opt):
    padded = 0
    for state in opt["state"].values():
        for key in ("exp_avg", "exp_avg_sq"):
            t = state.get(key)
            if t is not None and t.dim() == 2 and t.shape[1] == OLD_OBS:
                state[key] = torch.cat([t, torch.zeros(t.shape[0], NEW_OBS - OLD_OBS, dtype=t.dtype)], dim=1)
                padded += 1
    assert padded == 4, f"expected to pad 4 optimizer tensors (actor + critic), padded {padded}"


def verify(old_ck, new_ck):
    from mlagents_envs.base_env import ActionSpec, DimensionProperty, ObservationSpec, ObservationType
    from mlagents.trainers.settings import NetworkSettings
    from mlagents.trainers.torch_entities.networks import SimpleActor, ValueNetwork

    def spec(n):
        return [ObservationSpec((n,), (DimensionProperty.NONE,), ObservationType.DEFAULT, "VectorSensor_size")]

    ns = NetworkSettings(normalize=True, hidden_units=128, num_layers=2)
    act = ActionSpec(continuous_size=5, discrete_branches=(2,))
    nets = {}
    for n, ck in ((OLD_OBS, old_ck), (NEW_OBS, new_ck)):
        actor = SimpleActor(spec(n), ns, act)
        actor.load_state_dict(ck["Policy"], strict=True)
        critic = ValueNetwork(["extrinsic"], spec(n), ns)
        critic.load_state_dict(ck["Optimizer:critic"], strict=True)
        nets[n] = (actor.eval(), critic.eval())

    torch.manual_seed(0)
    sd = old_ck["Policy"]
    p = NORM_PREFIXES[0]
    mean = sd[p + "running_mean"]
    std = (sd[p + "running_variance"] / sd[p + "normalization_steps"]).sqrt()
    obs_old = mean + std * torch.randn(4096, OLD_OBS)
    obs_new = torch.cat([obs_old, 20 * torch.randn(4096, NEW_OBS - OLD_OBS)], dim=1)

    with torch.no_grad():
        a_old, c_old = nets[OLD_OBS]
        a_new, c_new = nets[NEW_OBS]
        enc_old, _ = a_old.network_body([obs_old])
        enc_new, _ = a_new.network_body([obs_new])
        mu_old = a_old.action_model._continuous_distribution.mu(enc_old)
        mu_new = a_new.action_model._continuous_distribution.mu(enc_new)
        lg_old = a_old.action_model._discrete_distribution.branches[0](enc_old)
        lg_new = a_new.action_model._discrete_distribution.branches[0](enc_new)
        v_old = c_old.critic_pass([obs_old])[0]["extrinsic"]
        v_new = c_new.critic_pass([obs_new])[0]["extrinsic"]

    diffs = {
        "continuous action means": (mu_old - mu_new).abs().max().item(),
        "kick logits": (lg_old - lg_new).abs().max().item(),
        "value estimates": (v_old - v_new).abs().max().item(),
    }
    for name, d in diffs.items():
        print(f"  max difference in {name:24s}: {d:.2e}")
    assert all(d < 1e-4 for d in diffs.values()), "outputs differ: conversion is wrong"
    print("  OK: the 46-input network behaves exactly like the 1v1 network.")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--source", default=None, help="1v1 checkpoint.pt (default: results/ or saved_models/ selfplay_1v1_01)")
    ap.add_argument("--name", default="3v3_base")
    args = ap.parse_args()

    source = args.source
    if source is None:
        for c in ("results/selfplay_1v1_01/FootballAgent/checkpoint.pt",
                  "saved_models/selfplay_1v1_01/FootballAgent/checkpoint.pt"):
            if os.path.exists(c):
                source = c
                break
    if source is None or not os.path.exists(source):
        raise SystemExit("1v1 checkpoint not found; pass --source")

    print(f"Source: {source}")
    old_ck = torch.load(source, map_location="cpu")
    print(f"  trained for {int(old_ck['global_step']['_GlobalSteps__global_step'].item())} steps, "
          f"{old_ck['Policy'][FIRST_LAYER].shape[1]} observations")
    new_ck = torch.load(source, map_location="cpu")

    expand_network(new_ck["Policy"])
    expand_network(new_ck["Optimizer:critic"])
    expand_optimizer(new_ck["Optimizer:value_optimizer"])
    new_ck["global_step"]["_GlobalSteps__global_step"].zero_()

    print("Verifying...")
    verify(old_ck, new_ck)

    out = os.path.join("results", args.name, "FootballAgent", "checkpoint.pt")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    torch.save(new_ck, out)
    backup = os.path.join("saved_models", args.name, "FootballAgent", "checkpoint.pt")
    os.makedirs(os.path.dirname(backup), exist_ok=True)
    shutil.copyfile(out, backup)
    print(f"Wrote {out}\n  and {backup}")


if __name__ == "__main__":
    main()
