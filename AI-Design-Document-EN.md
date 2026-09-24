# AI Design Document — Coordinated Tank Platoon

**Course**: AP6416 / CE6127 Game Artificial Intelligence
**Scene**: `MainAI.unity` · **Unity**: 2022.3.39f1 · **Date**: 2026-09-25

> A focused technical report, not a traditional game design document. Everything below concerns **NPC AI decision-making**.
>
> **Compliance**: all AI-related changes live in `.cs` files and comments under `Assets/Scripts/AI/**`. `Assets/Prefabs/**`, `Assets/Scenes/**`, `ProjectSettings/**` and `Packages/**` are unchanged from the course baseline (verifiable with `git diff`); package versions and every existing health, speed and damage value are untouched.

---

# 1. AI Design Overview

## 1.1 Objectives: the scoring rules dictate the design priorities

The match scoring is the AI's objective function — not "looking good in play":

- Player destroyed before all AI tanks are destroyed → **AI earns 3 points**
- For every AI tank the player destroys → **player earns 1 point**
- Time expires with the player alive → **AI earns 0 points**

This yields the priority ordering that shapes everything else:

| Priority | Goal | Rationale |
|---|---|---|
| **1** | **Destroy the player** | Only a kill scores the full 3 points; a timeout scores 0 |
| **2** | Preserve AI tanks | Every AI lost hands the opponent a point — pure negative return |
| **3** | Self-preservation (below 1 and 2) | Trading distance when damaged has value, but never at the cost of a kill |

**The key consequence: this is a match you must attack to win.** A passive platoon has an expected score of 0, so the whole AI is offence-oriented. `MinFireRange = 0` — fire even at point-blank range, accepting self-damage — is the most direct expression of that priority.

## 1.2 Two core contributions

The technical weight of this design sits in two places, developed in the sections noted.

### Contribution 1: Firing prediction (§2.4)

The shell is a **pure projectile** and takes 0.4–1.3 s to reach its target. Aiming at the player's *current* position therefore misses almost every time. We built a complete **ballistic and lead-solving pipeline**:

- The player's motion is **stepped forward** at a fixed timestep to produce a future **curved path**
- The point where the shell and the player **arrive simultaneously** is solved for, with the timing error **interpolated to its exact crossing**
- The **launch force** that lands the shell on exactly that point is solved for from the range

This is the direct source of our hit rate, and where most of this round's fixes went (§4.3).

### Contribution 2: Anti-kiting encirclement (§3.4)

The single most effective evasion tactic in this assignment is **circling the central building cluster**. Under equal-speed pursuit, that collapses the platoon into single file directly behind the player, where it can never land a shot.

We built **kiting detection plus ring-coordinate encirclement**: once the player is detected circling, the platoon switches from Cartesian pursuit to ring-coordinate interception, using **counter-orbiting** and **inner-radius cutting** to restore convergence.

## 1.3 Roles

The three tanks form one pusher plus two flankers. Roles are assigned by **round-robin over the platoon index** (`m_PlatoonIndex % 3`), so they are independent of spawn order.

| Role | Purpose | Behaviour |
|---|---|---|
| **Pusher** | Frontal pressure | Engagement radius 8 m, closes directly |
| **LeftFlank** | Left-side envelopment | Radius ×1.25 (10 m); orbit direction reversed |
| **RightFlank** | Right-side envelopment | Radius as LeftFlank; orbit direction opposite to it |

**Why one pusher and two flankers.** Three equal-speed tanks all driving at the front will inevitably converge into single file behind the player. Splitting the platoon into one frontal pressure point and two lateral envelopment points makes the player's escape directions **conflict with each other** — dodging left meets the left flank, dodging right meets the right flank.

## 1.4 Strategy in one line

> **The FSM decides which phase I am in; the Utility layer decides whom to shoot at and how far to stand; the platoon-level ring coordinates decide how we surround him.**

Three threads: frontal pressure → flank envelopment → anti-kiting encirclement.

---

# 2. AI Architecture & Logic

## 2.1 Technique selection: each permitted approach has a distinct job

The assignment permits any combination of FSM, Behaviour Trees and Utility AI. We use three techniques, but each one is used where the alternatives would be worse:

| Technique | Where it is used | Why this one |
|---|---|---|
| **FSM** | Phase transitions of a single tank | The phases are **discrete, mutually exclusive, with explicit entry conditions**, and there are only four. A Behaviour Tree would add hierarchy without adding capability |
| **Utility AI** | Target selection, orbit radius | Both are **continuous trade-offs** (closer is better; lower health should stand further off). Hard-coded thresholds would degrade into a pile of `if-else` |
| **Platoon-level coordination** | Encirclement role assignment | Requires **consistent decisions across tanks**. The FSM and Utility layers are both per-agent, so this needed its own shared-state layer |

We did not use a Behaviour Tree: this design has no multi-step stateful task sequences, and the modularity requirement is already met by the state machine plus a shared detector.

## 2.2 FSM: states and transitions

```
   Idle ──target──→ Chase ──LoS and ≤0.8× range──→ Attack
     ↑                ↑                                │
     │                └── >0.95× range or LoS lost >0.75 s ┘
     └── Patrolling ──target──┘
```

**Two design decisions matter here.**

**① Hysteresis.** Attack is entered at `0.8 × MaxFireRange` but only left at `0.95 ×`. The gap between the two thresholds is a dead band 15% of maximum range wide, so a player hovering at the boundary cannot make the state oscillate. Loss of line of sight works the same way — tolerated for 0.75 s rather than exiting on a single blocked frame.

**② The engagement test is "can I shoot", not "am I close enough".** The original gate was `distance ≤ OrbitRadius() × 1.2` (about 9.6 m). The problem: while the player kites behind a building, that distance is **never reached**, so the tank stayed in Chase for the entire round — and in Chase the hull faces its direction of travel, so the firing alignment check never passed. The result was a tank that could not catch the player, could not shoot, and scored 0 for the round.

The new gate is `line of sight and ≤ 0.8 × range`, so the tank enters the engagement posture as soon as a shot is genuinely available. **This is the single most direct contributor to our hit rate.**

## 2.3 Utility AI: target selection and orbit radius

**Target selection.** Each living player tank is scored and the highest score wins. The current scoring function is `straight-line distance` (minimised), which reduces to "shoot the nearest living target" because the match has only one player tank. The structure, however, is a general utility framework: `score` can accumulate terms for line-of-sight quality, target weakness or threat level **without touching the FSM** — which is precisely why Utility AI was chosen here.

**Orbit radius.** A textbook continuous trade-off:

```
OrbitRadius = EngageRadius(8 m) × role factor (pusher 1.0 / flanker 1.25) × health factor (HP < 35% → 1.4)
```

**Why damaged tanks back off.** Every AI lost hands the player a point. A damaged tank that keeps closing trades survival for a little more damage output — and in practice it will be destroyed and concede that point. Below 35% health it backs off instead: **trading shots for survival**, betting that the other two can finish the kill. This is a **continuous, tunable** trade-off rather than a hard-coded "flee when low" state, which would flip back and forth across the health threshold.

## 2.4 Contribution 1: firing prediction

### 2.4.1 The problem

The shell is a **pure projectile** (`Shell.prefab` has `m_Drag = 0`, verified), the barrel elevation is a fixed **+10°**, and the launch height is **1.7 m**. Reaching a target 20 m away takes about 0.85 s, during which the player moves roughly 10 m. **Aiming at the current position misses by construction.**

### 2.4.2 Launch force: the intercept height determines the muzzle velocity

Given a horizontal distance `d`, we solve for the muzzle velocity at which the shell reaches a specified height at `d`:

```
v² = g·d² / (2·cos²θ·(d·tanθ + (1−k)·h))        k = PassHeightFactor
```

There is a counter-intuitive but important property here: **`PassHeightFactor` is not a "force knob" but an "intercept-height knob."** The closer the factor is to 1, the faster the shell — because the extra velocity is exactly consumed by having to climb to a higher plane. Measurements favour **1.0** (a 7% faster shell); see §4.3.

### 2.4.3 Lead solving: interpolating to the simultaneous-arrival point

`PredictTargetPoint()` runs a **fixed-step loop** that carries the player's motion forward 1.5 s in 40 steps of 0.0375 s. Each step does exactly two things — **rotate the velocity direction by the player's angular velocity, then advance a short distance along it**:

```
pos = player position;   vel = player velocity
repeat 40 times (step = 0.0375 s):
    vel  rotated by player's angular velocity over 0.0375 s   // player is turning
    pos += vel × 0.0375 s                                     // advance a short distance
```

For each resulting sample `P(t)` we compute a **signed timing error**:

```
f(t) = rotateTime + flightTime − t
```

- `f > 0` → the shell arrives **late** (the player has already passed that point)
- `f < 0` → it arrives early
- `f = 0` → **shell and player arrive together — this is the point to aim at**

We then **linearly interpolate** between the bracketing pair of samples where `f` changes sign, rather than taking whichever sample happened to be nearest.

> **Why the horizon is 1.5 s**: the solved `t` must cover the longest flight time, and the shell takes 1.32 s to fly the 39.1 m maximum range. **Shortening it would directly remove long-range fire capability** — a 0.6 s horizon could only engage targets inside 10 m.

### 2.4.4 Firing gate

`TryFire()` checks each condition in turn and only fires when all pass: target present → cooldown elapsed (0.7 s) → within range → **aim tolerance** → line of sight → launch.

Whether that aim tolerance is an **angle** or a **distance** is the most important fix of this round (see §2.5 and §4.3).

### 2.4.5 Evolution: the prediction scheme was rewritten once

The current design did not appear fully formed. The first version (commit `b7ca61d`) worked by **estimating a flight time from the current position, converting it to a lead via flight time × player velocity, then re-estimating the distance** — repeated eight times:

```csharp
Vector3 predicted = Target.position;
for (var i = 0; i < 8; ++i)
{
    // Use horizontal distance so the flight time matches the horizontal speed component.
    Vector3 delta = predicted - FireTransform.position;
    delta.y = 0f;
    float horizontal = delta.magnitude;
    float speed = Mathf.Max(LaunchForceForDistance(horizontal), 0.1f) * Mathf.Cos(m_BarrelPitchRad);
    float flightTime = horizontal / Mathf.Max(speed, 1f);
    predicted = Target.position + TargetVelocity * flightTime;
}
```

Note the final line: **the lead is simply `Target.position + TargetVelocity × flightTime`** — a straight line.

**It had two fundamental weaknesses:**

1. **The motion model was linear.** Using only `TargetVelocity` **assumes the player never turns**. The moment the player turns, the predicted point drifts off — and circling obstacles is precisely the tactic players use most.
2. **The force formula targeted the ground.** `LaunchForceForDistance` lands the shell at `y = 0`, i.e. at ground level rather than hull height, and its flight time is correspondingly too long.

The second version (commit `e9aca73`) replaced this with the current scheme: the player's **angular velocity** is included and a **curved** path is stepped out (§2.4.3), while the force model moved to `MaxForceWithoutOvershoot`, whose intercept height is controllable.

| | First version `b7ca61d` | Current |
|---|---|---|
| Player motion model | **Linear** (velocity only) | **Curved** (velocity + angular velocity) |
| **Selection criterion** | **None** — returns whatever the 8th iteration produced | **Earliest time at which a hit is possible** |
| Solver | 8 fixed re-estimations | 40-step extrapolation with **sign-change interpolation** |
| Force model | Landing at `y = 0` (the ground) | Controllable intercept height (**the hull**) |
| Hull rotation time | Not accounted for | Included as `rotateTime` |
| Principal weakness | Drifts off as soon as the player turns | Constant-velocity assumption (§4.4) |

**The third difference is not about accuracy but about what is being selected — and it is the most fundamental of the three.**

The first version had **no selection criterion at all**: it re-ran the same estimate eight times and returned the eighth result, without checking convergence and without judging whether the resulting instant was **actually reachable**. The iteration count was hard-coded, so the quality of the answer depended entirely on where those eight steps happened to land.

The current version **does have an explicit criterion**: it scans the whole 1.5 s horizon and returns **the earliest instant at which a hit is possible** — the **first** sign change of `f`.

In other words, it no longer answers "where will the player be at some time", but "**from when do I have a chance of hitting him**". That matches the semantics of shooting far better — **fire as early as you can**, compressing the player's reaction time. The old version could just as easily return a very late instant (by which point the player is long gone) or a point that cannot be reached at all.

> **Why this evolution matters.** The first version's failure is itself evidence that **"the player will turn" is a factor this scenario must model**. That same observation is what later makes anti-kiting detection (§3.4) possible at all — the player's motion is a **regular curve**, not random jitter, which is what allows it to be recognised by "accumulated heading plus circle fitting". Two apparently independent changes rest on the same insight.

## 2.5 Firing tolerance: why it is deliberately loosened at close range

The tolerance is **piecewise**, and it is deliberately loose at short range — by design, not as a compromise.

```
d ≤ 8 m : tolerance = AimToleranceDeg(3.5°) × close-range widening (up to 6×)
d > 8 m : tolerance = atan(AimLateralTolerance(0.75 m) / d)     ← fixed lateral allowance
```

**Three reasons for loosening at close range:**

1. **Splash damage compensates for imprecision.** The shell's explosion radius is **3.33 m**. At point-blank range even a non-direct hit covers the target, so the precision simply is not needed.

2. **The bearing rate is too high for precise alignment to be sustainable.** A player strafing at 12 m/s produces a bearing rate of **86°/s at 8 m**. The hull's angular speed limit is 180°/s, so it can keep up — but holding a 1°-class alignment while tracking at that rate is not realistic.

3. **Suppression and movement denial.** Since precise single shots are not worth it up close, **maintaining fire coverage** is the better play: continuous fire pressures the player and **compresses the space available to them** — every dodge forces a direction change, and those directions are exactly what the two flankers are blocking. **Driving the player into the encirclement with fire** serves the platoon objective better than chasing a single guaranteed hit.

**At long range the tolerance must tighten**, because lateral error is `distance × tan(angle)` and therefore grows linearly. The old **constant angle** (2.45°) exceeded the tank's effective collider radius (about 0.9 m) at roughly 22 m — **past 22 m the tolerance alone permitted a miss**. Switching to a fixed lateral allowance gives:

| Range | Lateral error (old) | Lateral error (new) | Tolerance (new) |
|---|---|---|---|
| 8 m | 0.49 m | 0.49 m (unchanged) | 3.50° |
| 20 m | 0.86 m | **0.75 m** | 2.15° |
| 30 m | 1.28 m | **0.75 m** | 1.43° |
| 39 m | 1.67 m | **0.75 m** | 1.10° |

**Why the allowance is 0.75 m rather than tighter.** The tighter the tolerance, the longer the hull must wait to be aligned before it may fire — so the rate of fire falls. At 0.5 m the drop in fire rate was clearly visible in play. At 0.75 m the allowance is still inside the tank's effective collider radius of about 0.9 m, so **the long-range accuracy fix stands while the firing window widens by 1.5×**. It is a measured balance between shooting accurately and shooting often.

> **Why the inside-8 m region is left alone**: the existing `8/d` scaling there **already implements the fixed-lateral-allowance idea** (lateral error stays between 0.49 m and 0.64 m). The new code extends that **existing idea** past 8 m rather than inventing a different one.

---

# 3. Platoon Coordination

## 3.1 Role assignment

Roles rotate by platoon index, so the three tanks are always one pusher, one left flanker and one right flanker — **independent of spawn order**, and no single loss can degrade the platoon into "all pushers". Orbit direction (`StrafeSign`) is −1 for the left flanker and +1 for the others, guaranteeing the two flankers come around the player from **opposite sides**.

## 3.2 Positioning

**Approach phase: spread laterally, do not queue.** An intercept point is computed first, then displaced along the **perpendicular to the player's velocity**.

The critical question here is *what the offset is anchored to*. It must be anchored to **the player's own direction of motion**. Anchoring it to the AI's own heading (as the original ±55° flanker offset did) means the offset **rotates with the tank**, so it never actually occupies the player's front. Anchoring to the player's velocity gives a reference that is **stable in world space**, which is what lets three tanks share one frame and genuinely spread out.

**Engagement phase: orbit.** A point on a circle centred on the player's current position with radius `OrbitRadius()` is returned, with the angle advancing at 25°/s. **The radius is the engagement distance**, so this behaviour maintains standoff by construction.

**Ally spacing.** Every generated destination passes through a post-process that pushes it away from any ally within 6 m.

> ⚠️ That pass **only constrains AI-versus-AI spacing** and imposes **no minimum distance from the player**. The AI will genuinely collide with the player — this is intentional (both interception and counter-orbiting aim for a head-on meeting), and the cost is that point-blank fire also takes splash damage from its own shell (§1.1, an accepted trade-off).

## 3.3 Attack and retreat

**Attack.** Once in Attack, movement is owned by the orbit point and the hull **always** faces the predicted point — unlike Chase, which only turns to aim when a shot is actually available — so the tank can fire at any moment.

**Retreat.** The only form is **radius retreat** (HP < 35% → radius ×1.4). There is no "leave the battlefield" behaviour: it would simultaneously forfeit kill opportunities and guarantee a 0-point round through inaction, which is not a rational trade.

## 3.4 Contribution 2: anti-kiting encirclement

### The problem: equal-speed pursuit has a "queueing" attractor

The most effective evasion tactic in this assignment is **circling the central building cluster**. If each of the three AI tanks simply drives at the player's current position, they converge into **single file directly behind the player** — because for any fleeing target, the shortest path always points at its tail. And that tail sits precisely in the building's **occlusion shadow**: no line of sight, no shots, 0 points for the round.

### Detection: two independent signals, both required

**① Accumulated heading change (primary signal)**

```csharp
s_CumHeading += Mathf.DeltaAngle(s_PrevHeading, heading);   // accumulated every frame
```

`DeltaAngle` returns the shortest signed angle between two headings, so a **circling player accumulates monotonically** (+360° per lap) while a **player weaving left and right cancels out towards zero**. This separates "circling" from "jitter" for free.

**② Least-squares circle fit over the last 3 seconds**

Twelve position samples are fitted with the **Kasa algebraic method** to recover the loop's centre and radius. The trick is that the circle equation `x² + z² = A·x + B·z + C` is **linear in A, B and C** — so fitting a circle reduces to solving a 3×3 linear system, one determinant evaluation, **with no iteration and no initial guess**.

**Why not the instantaneous turn centre `v/ω`.** Going around a square building, the player's path is a **rounded rectangle**, not a circle. The instantaneous turn centre is the centre of whichever corner is currently being rounded, so it **jumps to a different corner of the building** every time the player rounds one. A least-squares fit over a full 3 s window averages the corners out and keeps the centre stable; it also treats "circling a building", "circling rocks" and "circling open ground" identically, so **no obstacle detection is needed**.

**Guards against false positives.** The fit is rejected if the samples are collinear (the player is going straight), if the radius falls outside [5, 45] m, or if the RMS residual is too large (it is not really a circle). There is also **hysteresis**: entry requires at least **100°** of accumulated turn, while exit only requires falling below **55°** — preventing the state from toggling rapidly when the turn rate sits near the threshold.

> **Shared implementation**: the detector is a `static` class advanced once per frame, so all three tanks read **the same** loop centre. This is required — role assignment compares "who is furthest behind", and independent centres would order the platoon inconsistently.

### Solution: switch to ring coordinates

Everything is then expressed as an **angle** and a **radius** relative to the fitted centre `Pivot`. Two mechanisms restore convergence:

**Mechanism A — counter-orbiting.** The player goes clockwise, the AI goes anticlockwise. The angular gap closes at `ω_p + ω_i`. This is a **topological guarantee**: on a closed loop, two bodies moving in opposite directions must meet, **regardless of speed**.

**Mechanism B — cutting inside.** Same direction, but on a **smaller radius**. Since `ω = v/R` and both parties move at 12 m/s, the smaller radius has the higher angular speed:

| | Radius | Angular speed |
|---|---|---|
| Player | 15 m | 45.8°/s |
| AI cutting inside | 9 m | **76.4°/s** |
| | | **net gain 30.6°/s** |

**Choosing between them is a calculation, not a heuristic.** With the AI trailing by `δ` degrees and `k = R_player / R_inner`, setting the two strategies' closing times equal gives:

```
δ_switch = 180 · (k−1) / k        at k = 1.67  →  δ ≈ 72°
```

Below 72° of lag, cutting inside closes faster; above it, turning around and meeting head-on does.

**One forced rule:** **the tank furthest behind always reverses, and the one furthest ahead always cuts inside.** The reason is that when the platoon has just been shaken off, all three tanks have small `δ`, so the 72° rule would make **all three choose the inner cut** — same direction, and they fall back into single file. The forced rule guarantees at least one tank is always travelling the other way.

**Decision stability.** A chosen move is held for 1.5 s, and the switching threshold carries a ±15° dead band.

### An easy implementation trap: issue one short waypoint at a time

`NavMeshAgent.SetDestination()` takes the **shortest path**. Handing it a point 180° around the ring lets the agent pick its own direction of travel — possibly the exact opposite of the one you chose, rendering the whole scheme useless. So the code issues **only a 50° step ahead**, re-issued every 0.2 s, **using high-frequency short steps to force the agent to travel the ring in the commanded direction**.

**Exit condition.** As soon as there is line of sight and the distance is ≤ 14 m, control returns to normal engagement. Otherwise a tank that has just met the player head-on would keep circling the ring and **walk away from them**.

---

# 4. Evaluation & Reflection

## 4.1 Testing evidence

Method: consecutive 3-round matches in `MainAI.unity`, with `GameManager` exporting `Excel/*.xlsx` automatically (columns include match index, AI points, player points, round count and machine name).

**13 matches recorded on 2026-09-25:**

| Metric | Result |
|---|---|
| **Player points** | **0 in all 13 matches** — the player never destroyed a single AI tank |
| AI points | > 0 in all 13 matches |
| Strongest run | 05:08–05:17, six consecutive matches each scoring the full 9 points (increments 9/9/9/9/9/9) |

The strongest result: six consecutive matches, each winning all three rounds — **18 rounds won with zero losses**. Earlier matches scored only 3 or 6 points (rounds where the time limit expired before the player was destroyed), so "reliably destroying the player within three rounds" was a state reached later rather than a given.

> ⚠️ **Limitation of this evidence.** All 13 matches were recorded between **03:53 and 05:17**, whereas this round's aiming and tolerance fixes were committed at **05:18:48**. **Not one match was played on the fixed build**, so the two sets cannot be compared. Demonstrating the effect of this round's fixes requires a fresh set of matches.

## 4.2 Strengths

1. **Resistant to the strongest evasion tactic.** The most effective escape in this assignment has a dedicated detector and counter, rather than being left to chance.
2. **Robust detection.** The two signals are complementary: circle fitting rejects zig-zag motion, accumulated heading rejects wide gentle curves. Either alone would misclassify.
3. **Consistent platoon decisions.** The loop centre is shared and computed once per frame, so all three tanks assign roles in the same coordinate frame.
4. **Traceable trade-offs.** The 72° threshold, the 1.25× flanker radius and the 35% health line are all derived or measured, not magic numbers.
5. **Clean compliance.** Every change sits in `Assets/Scripts/AI/**`; prefabs, scenes and `ProjectSettings` are unchanged from the baseline.

## 4.3 Three defects fixed this round

All three are of the **"correct model, biased implementation"** kind — the ballistic formula, the flight-time formula and the low-pass filter were each right in themselves; the error was in how they were applied.

### Defect 1: the lead was systematically short

The original loop **returned early** as soon as `|f| ≤ 0.05 s`. But `f` **decreases monotonically through zero**, and the loop scans `t` from small to large, so **the first sample to fall inside the tolerance band is necessarily on the positive side** — it always returned a "shell arrives late" point, corresponding to a positional error of **0.33–0.60 m**.

Fix: run the loop to completion, track the minimum `|f|`, and **interpolate** between the bracketing pair where the sign changes to find the exact crossing. The bias drops to ≈ 0.

> **This bias cannot be fixed by sampling more densely** — `0.05` is in **seconds**, so it is a floor. Measured: 40 samples → 0.374 m; 200 samples → **0.588 m (worse)**.

### Defect 2: the tolerance was a constant angle

See §2.5. Lateral error grows linearly with range and exceeded the effective collider radius (0.9 m) at about 22 m — **past 22 m the tolerance alone permitted a miss**, which is the direct source of shells landing behind the player. After switching to a fixed lateral allowance, the error at 39 m falls from 1.67 m to 0.75 m.

> The allowance sits at 0.75 m rather than tighter because a tighter tolerance lowers the rate of fire; at 0.5 m the drop was clearly visible in play.

### Defect 3: the velocity estimate's responsiveness was measured in frames, not seconds

Lead prediction needs the player's velocity. `TickTargetTracking()` estimates it by differencing consecutive frames and then low-pass filters it. The filter factor was written as **"advance 40% every frame"** — so its **time constant was measured in frames, not seconds**:

| Frame rate | Time constant |
|---|---|
| 200 fps | ~10 ms |
| 60 fps | 32.6 ms |
| 30 fps | ~65 ms |

The same filter left the velocity estimate **six times more sluggish** at a low frame rate. And since **lead = velocity × flight time**, a lagging velocity estimate is a wrong lead.

**This is the direct cause of the AI being noticeably stronger in the Editor and noticeably weaker in the standalone build.** The causation was confirmed by measurement: shrinking the build's window to raise its frame rate immediately restored the AI's strength.

Fix: the filter factor is now delta-time compensated, so its time constant is the same at **every** frame rate. The value was then set by measurement — **less smoothing proved more accurate**, meaning lag hurts more than noise in this scenario.

### A hypothesis the measurements disproved (recorded honestly)

We initially reasoned that `PassHeightFactor` should drop from 1.0 to 0.7. At 1.0 the shell arrives exactly at the **top face** of the tank collider (the collider spans 0 to 1.7 m, the same as the launch height), leaving only the shell capsule's 0.195 m as vertical margin — so any error at all appeared to send the shell over the roof.

But **we had underestimated the tolerance by an order of magnitude**. The real tolerance is not the 0.195 m radius but **how far short the aim can be before the descending trajectory re-enters the collider**. Since `tan 10° = 0.176`, moving 1 m closer raises the shell by only about 0.18 m, giving a measured tolerance of roughly **1.1 m**. The post-fix lead error is about 0.1 m — far inside it.

So the extra 0.9 m of margin that 0.7 buys is **never used**, while it costs a **7% slower shell**. **Measurements kept 1.0.** It is recorded here because the reasoning chain looked rigorous and was wrong — a reminder to check magnitudes before acting on an argument.

## 4.4 Limitations

Ordered by magnitude of impact:

- 🔴 **The velocity difference is aliased.** The player's position is advanced by the physics engine at **50 Hz** (`Rigidbody.MovePosition` in `FixedUpdate`) and is **not interpolated for rendering** (`m_Interpolate: 0`), while the AI samples it at the **render** rate. Each frame's displacement is therefore a whole number of physics steps, but the code divides it by a **render frame time** — a **unit mismatch**. At 60 fps the resulting speed reads 14.4 m/s where the true value is 12, and drops to zero on frames that contain no physics step. **The low-pass filter is currently papering over this bad input.** Located but **not yet fixed**; the fix is to sample on the physics step and drop the filter entirely. **The magnitude of this one is not yet measured.**
- 🔴 **The prediction assumes the player holds their current linear and angular velocity.** When the player accelerates, decelerates or changes turn direction, the prediction is off by up to **metres** — far beyond the 0.1–0.75 m corrections above. This and the item above are the joint dominant remaining sources of uncertainty in our hit rate.
- 🟡 **Physical ramming and self-damage.** Both interception and counter-orbiting aim for head-on meetings, and ally spacing only constrains AI-versus-AI distances. With `MinFireRange = 0`, point-blank fire takes splash damage from the AI's own shell (a deliberate trade-off).
- 🟡 **God's-eye perception.** The AI reads the player's position and velocity directly, with no field of view, occlusion or range limit. Line of sight is still checked before firing, so it cannot shoot through walls — but strictly speaking the AI is aware of a target it cannot see.
- 🟢 **`AcquireRange` is a dead field**, read nowhere in the project; `SelectTarget()` picks the nearest target unconditionally.
- 🟢 **The barrel elevation is cached in `Awake()`**, while the shell launches along the live barrel orientation. In normal play `RotateTowards` levels the hull every frame so the two agree, but hull pitch from a collision or a slope would desynchronise them.

## 4.5 Reflection: the preconditions this strategy depends on

We should acknowledge an element of winning on an uneven footing.

**In this game the player's tank can only fire along its hull's forward axis**, so aiming depends entirely on rotating the hull. The player therefore **cannot move in one direction while firing in another**, which makes it hard to sustain damage output while moving, or to perform manoeuvres like strafing out of cover to fire and ducking back in.

The consequence is that **the AI comes under far less fire than in a typical shooter**. That is what let us put killing ahead of survival: `MinFireRange = 0` permitting self-damage, a health threshold as low as 35%, and committing all three tanks at once during encirclement.

**But that precondition is not general.** In a more conventional design — where the player can strafe out of cover, fire, and duck back, or circle and shoot at the same time — the AI would be under sustained fire, and then:

- the point-blank self-damage from `MinFireRange = 0` turns from an acceptable cost into a **net loss**
- the health threshold for backing off needs to rise substantially
- more fundamentally, **charging in with all three tanks concentrates the platoon inside the player's field of fire**, which calls for genuine survival coordination — alternating cover, bounding overwatch, staggered advance

In other words, **this design is optimised for one specific constraint: that the player's mobility and firepower are tightly coupled.** Relax that constraint and the priority ordering (kill ≫ survive) has to be re-evaluated. This also explains why the design **does not treat AI survival as a first-class objective** — not an oversight, but a reasonable trade under the given rules.

## 4.6 Improvements

1. **Remove the aliasing in the velocity difference** (see the first item of §4.4). Sample on the physics step and divide by `Time.fixedDeltaTime`, so the input is correct first; the smoothing filter can then be deleted outright — one change removing the aliasing, the lag and the filter together. **This is the highest-value item.**
2. **Remove the constant-velocity assumption.** Options: handle player turns conservatively (rather than risk a wild shot, hold fire), shorten the effective prediction window, or bound the error introduced by turning.
3. **Limited perception.** Gate `SelectTarget()` on a field of view or range instead of the current god's-eye model. The assignment does not forbid omniscience, but explicitly modelling perception is a genuine AI-quality improvement.
4. **Survival coordination** (if the precondition above relaxes). Alternating cover, bounding advance, damaged tanks disengaging.
5. **Recompute the barrel elevation at runtime**, removing the model desynchronisation caused by hull pitch.
6. **Remove the dead `AcquireRange` field.**
7. **Collect post-fix test data** (necessary) — all existing data predates the fix commit.

---

# Appendix A: Code changes

| File (`Assets/Scripts/AI/`) | Status | Purpose |
|---|---|---|
| `Tank/StateMachine/TankSM.cs` | Modified | State machine, utility scoring, ballistic solving, firing gate, encirclement methods |
| `Tank/StateMachine/OrbitTracker.cs` | **New** | Kiting detection (accumulated heading + Kasa circle fit) |
| `Tank/StateMachine/States/ChaseState.cs` | **New** | Approach and lateral spread |
| `Tank/StateMachine/States/AttackState.cs` | **New** | Engagement orbit |
| `Tank/StateMachine/States/IdleState.cs` | Modified | Face the target and decide immediately |
| `Tank/StateMachine/States/PatrollingState.cs` | Modified | Search patrol |

**Unchanged**: `Assets/Prefabs/**`, `Assets/Scenes/**`, `ProjectSettings/**`, `Packages/**`.

# Appendix B: Key parameters

| Parameter | Value |
|---|---|
| Barrel elevation / launch height | +10° / 1.7 m |
| Tank collider vertical extent | 0 → 1.7 m (top face = launch height) |
| Launch force range / cooldown | 7.5–30 / 0.7 s |
| Maximum range | ≈ 39.1 m (1.32 s flight) |
| NavMesh speed / angular speed | 12 m/s / 180°/s |
| Kiting entry / exit threshold | 100° / 55° (accumulated heading, hysteresis) |
| Circle-fit sampling | 12 points / 3 s |
| Inner-cut switch threshold | 180(k−1)/k, k = 1.67 → **72°** |
| Ring waypoint step / handoff range | 50° / 14 m |
| Role hold time / dead band | 1.5 s / ±15° |
| Engagement radius | 8 m (pusher) / 10 m (flanker) |
| Damaged retreat | HP < 35% → radius ×1.4 |
| Aim tolerance | ≤ 8 m: 3.5°; > 8 m: 0.75 m lateral (inside the ~0.9 m effective collider radius) |
| Velocity smoothing time constant | 8.2 ms (frame-rate independent; set by `VelocitySmoothingReferenceFps` = 240) |
| Intercept-height factor | 1.0 |

# Appendix C: Supporting documents

Both are in Chinese, in the repository root.

- `射击流程.md` — method-by-method walkthrough of the firing chain, including the ballistic derivation
- `codex-apply-anti-kiting.md` — design specification and implementation brief for the anti-kiting encirclement
