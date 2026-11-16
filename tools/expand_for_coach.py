"""
Make the coach-stage starting models from the finished 3M-step 3v3 models.

For each team (NoPass, HalfPass, AllPass) it reads results/3v3_<Team>/FootballAgent/checkpoint.pt and:

  1. Adds the 3 coach inputs (46 -> 49 observations: am-I-the-chaser, vector to my slot x/z).
     Their weights start at ZERO, so the network ignores them at first and plays exactly as before.
     The normaliser gets starting statistics for them, and its sample count is capped so the new
     statistics can adapt (with millions of old samples they would barely move).
  2. Halves the two MOVEMENT outputs of the policy (the means of moveX / moveZ, weights and bias).
     Why: after 3M steps most movement outputs are far beyond ML-Agents' clip at +-3, so every sample
     comes out as exactly +-1 and the policy can no longer try other ways of moving. Halved, they are
     back inside the range where sampling matters. The game multiplies the movement by move_gain = 2
     (set in the coach configs), so where the old output was not clipped the movement is exactly the
     same; measured in real games: speed unchanged, direction changes by 3 degrees (median).
  3. Pads the Adam optimiser state for the 3 new inputs.
  4. SLOT PRIOR: the policy's weights for the slot vector (inputs 47, 48) start as SLOT_PRIOR (1.5) x its
     learned weights for the vector to the ball (inputs 7, 9), adjusted for the different normaliser
     scale. Why: with zero weights the converged policy learned to use the new inputs far too slowly
     (pilot: 120k steps with a large role reward, response to the slot ~1% of the response to the ball,
     role adherence unchanged). With the prior, a supporting player at first treats its slot like a
     "second ball", so its existing chasing skill pulls it ~60% of the way to the slot (it runs to
     ball + 1.5 x slot vector); training then refines this. The chaser's slot input is always zero, so the
     chaser plays exactly as before. The value network's new weights stay zero.

Then it builds the real ML-Agents networks and checks: with zero slot input, value estimates and kick
outputs are identical and movement means exactly half; with a slot input, the policy outputs equal those
for a ball moved by 1.5 x the slot vector.

Usage (repo root, venv active):   python tools/expand_for_coach.py [--teams NoPass HalfPass AllPass]
Writes results/3v3_<Team>_coachbase/FootballAgent/checkpoint.pt and a backup in saved_models/.
"""
import argparse
import os
import shutil

import torch

OLD_OBS = 46
NEW_OBS = 49
MOVE_SCALE = 0.5          # movement means are multiplied by this; the game uses move_gain = 1 / MOVE_SCALE
NORM_STEPS_CAP = 100_000

SLOT_PRIOR = 1.5          # see 4. above
BALL_INPUTS = (7, 9)      # vector to ball x, z
SLOT_INPUTS = (47, 48)    # vector to slot x, z

# Starting normaliser statistics for the 3 new inputs (measured in the coach pilot runs):
#   46 is-chaser flag: about 1 player in 3 -> mean 1/3, variance 2/9
#   47, 48 vector to slot x/z: mean 0, std 12 / 7 m
NEW_MEAN = [1 / 3, 0.0, 0.0]
NEW_VAR = [2 / 9, 12.0 ** 2, 7.0 ** 2]

NORM_PREFIXES = [
    "network_body.observation_encoder.processors.0.normalizer.",
    "network_body.processors.0.normalizer.",
]
FIRST_LAYER = "network_body._body_endoder.seq_layers.0.weight"
MU_W = "action_model._continuous_distribution.mu.weight"
MU_B = "action_model._continuous_distribution.mu.bias"


def expand_normalizer(sd, prefix):
    steps = sd[prefix + "normalization_steps"].clone()
    mean = sd[prefix + "running_mean"].clone()
    variance = sd[prefix + "running_variance"].clone() / steps     # stored as a sum of squares
    mean = torch.cat([mean, torch.tensor(NEW_MEAN, dtype=mean.dtype)])
    variance = torch.cat([variance, torch.tensor(NEW_VAR, dtype=variance.dtype)])
    capped = torch.minimum(steps, torch.tensor(NORM_STEPS_CAP, dtype=steps.dtype))
    sd[prefix + "normalization_steps"] = capped
    sd[prefix + "running_mean"] = mean
    sd[prefix + "running_variance"] = variance * capped.to(variance.dtype)


def expand_network(sd, slot_prior=0.0):
    for prefix in NORM_PREFIXES:
        expand_normalizer(sd, prefix)
    w = sd[FIRST_LAYER]
    assert w.shape[1] == OLD_OBS, f"expected {OLD_OBS} inputs, found {w.shape[1]}"
    w = torch.cat([w, torch.zeros(w.shape[0], NEW_OBS - OLD_OBS, dtype=w.dtype)], dim=1)
    if slot_prior:
        # Input i is normalised as (x - mean_i) / std_i. With slot mean 0, weight
        # prior * w_ball * std_slot / std_ball makes a slot offset x act exactly like moving the ball by prior * x.
        p = NORM_PREFIXES[0]
        std = (sd[p + "running_variance"] / sd[p + "normalization_steps"]).sqrt()
        for b, s in zip(BALL_INPUTS, SLOT_INPUTS):
            w[:, s] = slot_prior * w[:, b] * std[s] / std[b]
    sd[FIRST_LAYER] = w


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
    p = NORM_PREFIXES[0]
    sd = old_ck["Policy"]
    mean = sd[p + "running_mean"]
    std = (sd[p + "running_variance"] / sd[p + "normalization_steps"]).sqrt()
    obs_old = mean + std * torch.randn(4096, OLD_OBS)
    obs_new = torch.cat([obs_old, 20 * torch.randn(4096, NEW_OBS - OLD_OBS)], dim=1)

    # Slot inputs zero (the chaser), random is-chaser flag: must behave exactly like the old model.
    obs_new = torch.cat([obs_old, (torch.rand(4096, 1) < 0.5).float(), torch.zeros(4096, 2)], dim=1)
    with torch.no_grad():
        (a_old, c_old), (a_new, c_new) = nets[OLD_OBS], nets[NEW_OBS]
        enc_old, _ = a_old.network_body([obs_old])
        enc_new, _ = a_new.network_body([obs_new])
        mu_old = a_old.action_model._continuous_distribution.mu(enc_old)
        mu_new = a_new.action_model._continuous_distribution.mu(enc_new)
        lg_old = a_old.action_model._discrete_distribution.branches[0](enc_old)
        lg_new = a_new.action_model._discrete_distribution.branches[0](enc_new)
        v_old = c_old.critic_pass([obs_old])[0]["extrinsic"]
        v_new = c_new.critic_pass([obs_new])[0]["extrinsic"]

    diffs = {
        "movement means (x0.5)": (mu_old[:, :2] * MOVE_SCALE - mu_new[:, :2]).abs().max().item(),
        "kick means": (mu_old[:, 2:] - mu_new[:, 2:]).abs().max().item(),
        "kick logits": (lg_old - lg_new).abs().max().item(),
        "value estimates": (v_old - v_new).abs().max().item(),
    }
    # Slot prior: a slot offset must act exactly like moving the ball by SLOT_PRIOR x that offset
    # (small offsets, so the normaliser's clamp at +-5 std does not interfere).
    slot = torch.randn(4096, 2) * 3.0
    obs_slot = obs_new.clone()
    obs_slot[:, SLOT_INPUTS[0]] = slot[:, 0]
    obs_slot[:, SLOT_INPUTS[1]] = slot[:, 1]
    obs_ball = obs_new.clone()
    obs_ball[:, BALL_INPUTS[0]] += SLOT_PRIOR * slot[:, 0]
    obs_ball[:, BALL_INPUTS[1]] += SLOT_PRIOR * slot[:, 1]
    keep = (obs_ball[:, list(BALL_INPUTS)] - mean[list(BALL_INPUTS)]).abs().le(4.5 * std[list(BALL_INPUTS)]).all(1)
    with torch.no_grad():
        e1, _ = a_new.network_body([obs_slot[keep]])
        e2, _ = a_new.network_body([obs_ball[keep]])
        diffs["slot = moved ball (actor)"] = (a_new.action_model._continuous_distribution.mu(e1)
                                              - a_new.action_model._continuous_distribution.mu(e2)).abs().max().item()
        v1 = c_new.critic_pass([obs_slot[keep]])[0]["extrinsic"]
        v0 = c_new.critic_pass([obs_new[keep]])[0]["extrinsic"]
        diffs["critic ignores slot"] = (v1 - v0).abs().max().item()

    for name, d in diffs.items():
        print(f"    max difference in {name:26s}: {d:.2e}")
    assert all(d < 1e-3 for d in diffs.values()), "outputs differ: conversion is wrong"
    print("    OK")


def convert(team):
    source = f"results/3v3_{team}/FootballAgent/checkpoint.pt"
    if not os.path.exists(source):
        source = f"saved_models/3v3_{team}/FootballAgent/checkpoint.pt"
    print(f"{team}: {source}")
    old_ck = torch.load(source, map_location="cpu", weights_only=False)
    steps = int(old_ck["global_step"]["_GlobalSteps__global_step"].item())
    print(f"    trained for {steps} steps, {old_ck['Policy'][FIRST_LAYER].shape[1]} observations")
    new_ck = torch.load(source, map_location="cpu", weights_only=False)

    expand_network(new_ck["Policy"], slot_prior=SLOT_PRIOR)
    expand_network(new_ck["Optimizer:critic"])
    expand_optimizer(new_ck["Optimizer:value_optimizer"])
    new_ck["Policy"][MU_W] = new_ck["Policy"][MU_W].clone()
    new_ck["Policy"][MU_B] = new_ck["Policy"][MU_B].clone()
    new_ck["Policy"][MU_W][:2] *= MOVE_SCALE
    new_ck["Policy"][MU_B][:2] *= MOVE_SCALE
    new_ck["global_step"]["_GlobalSteps__global_step"].zero_()

    verify(old_ck, new_ck)

    name = f"3v3_{team}_coachbase"
    out = os.path.join("results", name, "FootballAgent", "checkpoint.pt")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    torch.save(new_ck, out)
    backup = os.path.join("saved_models", name, "FootballAgent", "checkpoint.pt")
    os.makedirs(os.path.dirname(backup), exist_ok=True)
    shutil.copyfile(out, backup)
    print(f"    wrote {out} (+ backup in saved_models)")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--teams", nargs="+", default=["NoPass", "HalfPass", "AllPass"])
    for team in ap.parse_args().teams:
        convert(team)


if __name__ == "__main__":
    main()
