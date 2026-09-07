# Codex 应用任务：反遛狗合围拦截（完整实现）

工程：`E:\unity\tanks-lab-student2`，分支 `lab`，Unity 2022.3.39f1
本文档自包含。按顺序执行，代码可直接落地。

---

## 0. 硬约束

| 允许 | 禁止 |
|---|---|
| 只改 / 新增 `Assets/Scripts/AI/**` 下的 `.cs` | 改任何 `.prefab` / `.unity` / `ProjectSettings/**` / `Packages/**` |
| 新参数用 `private const` | 新增 `public` 或 `[SerializeField]` 字段并 Apply 到 prefab |
| 运行时读写 NavMeshAgent 的 destination / stoppingDistance | 改 NavMeshAgent 的 radius / height / speed / angularSpeed / avoidancePriority |
| — | 改 health / speed / damage 数值，或 Rigidbody / Collider / NavMesh 烘焙 |

**动手前先提交基线：**

```bash
cd /e/unity/tanks-lab-student2
git checkout -b feature/anti-kiting
git add -A && git commit -m "baseline before anti-kiting encirclement"
```

完成后必须 **0 error / 0 warning**。所有新增代码要有注释。

---

## 1. 要解决的问题

玩家绕场景中央的建筑群转圈，三辆 AI 排成一列在后面追，追不上、没视线、打不出枪，回合结束 AI 0 分。

**根因**：追击是一个吸引子——对正在逃跑的目标，最短路径永远收敛到它正后方，所以三辆车必然被吸到尾部，而尾部正是障碍物的阴影区。现有 `ChaseState.PursuitPoint()` 里 `±55°` 的偏移是绕 **AI 自己的朝向** 转的（`direction` 是 AI→玩家），会跟着 AI 一起转，因此不改变这个吸引子。

**解法**：检测到玩家在绕圈时，从「笛卡尔坐标下的追击」切换到「环坐标下的合围」。

两个恢复收敛性的机制：

- **反向绕**：闭环上反向前进，角度差以 `ω_p + ω_i` 闭合。拓扑保证，与速度无关。
- **内切**：`ω = v / R`。线速度都是 12，但半径小的角速度大。玩家在 15 m 环上 `ω = 45.8°/s`，AI 切到 9 m 环上 `ω = 76.4°/s`，**每秒净赚 30.6°**。

选哪个可以算出来：设落后玩家 `δ` 度、`k = R_p / R_inner`，
`同向内切耗时 = δ / ((k−1)ω_p)`，`反向绕耗时 = (360−δ) / ((1+k)ω_p)`，令两者相等得

```
δ_switch = 180 · (k − 1) / k          // k = 1.67 时 ≈ 72°
```

再加一条强制约束：**落后最多的那辆无条件反向**，保证任何时刻至少有一辆在反方向（否则三辆车刚被甩开时 δ 都很小，规则会让它们全部同向 → 又排成一列）。

---

## 2. 新建文件：`Assets/Scripts/AI/Tank/StateMachine/OrbitTracker.cs`

绕圈检测器。三辆车共用同一份估计（静态类 + 每帧只算一次），保证角色分配一致。

**关键设计**：环心不靠检测障碍物，而是对玩家最近 3 秒的轨迹做**最小二乘圆拟合**。这样「绕建筑」「绕岩堆」「绕空地」一视同仁，而且对圆角矩形路径（贴着方形建筑走）也成立——用瞬时转弯中心 `v/ω` 会在建筑物拐角处乱跳，圆拟合不会。

```csharp
using UnityEngine;

namespace CE6127.Tanks.AI
{
    /// <summary>
    /// Class <c>OrbitTracker</c> detects that the target is kiting — circling a fixed point —
    /// and estimates the centre and radius of that loop.
    /// <para>
    /// The centre is recovered by least-squares circle fitting over the target's recent path
    /// rather than by probing for obstacles, so it works the same whether the player is
    /// circling a building, a rock cluster, or open ground. A rounded-rectangle path around a
    /// square building still fits well, whereas the instantaneous turn centre (v / omega) would
    /// jump between corners.
    /// </para>
    /// <para>
    /// The state is static and advanced once per frame so that every tank in the platoon reads
    /// exactly the same pivot, which is what makes the role assignment in
    /// <see cref="TankSM.DecideRingMove"/> consistent across the platoon.
    /// </para>
    /// </summary>
    internal static class OrbitTracker
    {
        private const int Samples = 12;                  // path samples kept
        private const float Window = 3f;                 // seconds of history
        private const float SampleInterval = Window / Samples;
        private const float EnterSweptDeg = 100f;        // swept heading needed to declare kiting
        private const float ExitSweptDeg = 55f;          // Schmitt trigger: lower bar to stay in
        private const float MinRadius = 5f;
        private const float MaxRadius = 45f;
        private const float StaleSeconds = 0.5f;

        private static Transform s_Target;
        private static int s_Frame = -1;
        private static float s_LastTickTime = -99f;
        private static readonly Vector3[] s_Pos = new Vector3[Samples];
        private static readonly float[] s_Heading = new float[Samples];
        private static int s_Index;
        private static int s_Filled;
        private static float s_NextSample;
        private static float s_CumHeading;               // unwrapped, monotonic while turning
        private static float s_PrevHeading;
        private static bool s_Orbiting;

        /// <summary>Whether the target is currently circling a fixed point.</summary>
        public static bool IsOrbiting => s_Orbiting;

        /// <summary>Centre of the loop the target is describing.</summary>
        public static Vector3 Pivot { get; private set; }

        /// <summary>Radius of the fitted loop.</summary>
        public static float Radius { get; private set; }

        /// <summary>Signed heading swept over the history window (degrees).</summary>
        public static float SweptDeg { get; private set; }

        /// <summary>+1 or -1: the direction the target travels around <see cref="Pivot"/>.</summary>
        public static float Sign => SweptDeg >= 0f ? 1f : -1f;

        public static void Reset()
        {
            s_Target = null;
            s_Index = 0;
            s_Filled = 0;
            s_CumHeading = 0f;
            SweptDeg = 0f;
            s_Orbiting = false;
            Pivot = Vector3.zero;
            Radius = 0f;
        }

        /// <summary>
        /// Advances the estimate. Safe to call from every tank: only the first call in a frame
        /// does any work.
        /// </summary>
        public static void Tick(Transform target)
        {
            if (s_Frame == Time.frameCount)
                return;
            s_Frame = Time.frameCount;

            if (target == null)
            {
                Reset();
                return;
            }

            // A new target, or a gap in ticking (round change), invalidates the history.
            if (target != s_Target || Time.time - s_LastTickTime > StaleSeconds)
            {
                Reset();
                s_Target = target;
                s_PrevHeading = target.eulerAngles.y;
                s_NextSample = Time.time;
            }
            s_LastTickTime = Time.time;

            // Unwrapped heading: a circling target accumulates monotonically, a weaving one
            // cancels out. This is the primary "is this kiting" signal.
            float heading = target.eulerAngles.y;
            s_CumHeading += Mathf.DeltaAngle(s_PrevHeading, heading);
            s_PrevHeading = heading;

            if (Time.time >= s_NextSample)
            {
                s_NextSample = Time.time + SampleInterval;
                s_Pos[s_Index] = target.position;
                s_Heading[s_Index] = s_CumHeading;
                s_Index = (s_Index + 1) % Samples;
                if (s_Filled < Samples)
                    ++s_Filled;
            }

            if (s_Filled < Samples)
            {
                s_Orbiting = false;
                return;
            }

            SweptDeg = s_CumHeading - s_Heading[s_Index];   // s_Index now points at the oldest entry

            bool fitted = FitCircle(out Vector3 centre, out float radius);
            if (fitted)
            {
                Pivot = centre;
                Radius = radius;
            }

            float swept = Mathf.Abs(SweptDeg);
            s_Orbiting = fitted && swept >= (s_Orbiting ? ExitSweptDeg : EnterSweptDeg);
        }

        /// <summary>
        /// Algebraic (Kasa) least-squares circle fit over the path history. Solving
        /// x^2 + z^2 = A*x + B*z + C is linear, so a single 3x3 solve is enough.
        /// Rejects fits that are collinear, out of range, or simply not circular.
        /// </summary>
        private static bool FitCircle(out Vector3 centre, out float radius)
        {
            centre = Vector3.zero;
            radius = 0f;

            // Centre the samples first so the normal equations stay well conditioned.
            float mx = 0f, mz = 0f;
            for (int i = 0; i < Samples; ++i)
            {
                mx += s_Pos[i].x;
                mz += s_Pos[i].z;
            }
            mx /= Samples;
            mz /= Samples;

            double suu = 0, svv = 0, suv = 0, su = 0, sv = 0, sw = 0, suw = 0, svw = 0;
            for (int i = 0; i < Samples; ++i)
            {
                double u = s_Pos[i].x - mx;
                double v = s_Pos[i].z - mz;
                double w = u * u + v * v;
                suu += u * u; svv += v * v; suv += u * v;
                su += u; sv += v; sw += w;
                suw += u * w; svw += v * w;
            }

            double n = Samples;
            double det = suu * (svv * n - sv * sv) - suv * (suv * n - sv * su) + su * (suv * sv - svv * su);
            if (System.Math.Abs(det) < 1e-6)
                return false;                                   // collinear: the target is going straight

            double d1 = suw * (svv * n - sv * sv) - suv * (svw * n - sv * sw) + su * (svw * sv - svv * sw);
            double d2 = suu * (svw * n - sv * sw) - suw * (suv * n - sv * su) + su * (suv * sw - svw * su);
            double d3 = suu * (svv * sw - svw * sv) - suv * (suv * sw - svw * su) + suw * (suv * sv - svv * su);

            double a = 0.5 * (d1 / det);
            double b = 0.5 * (d2 / det);
            double rr = (d3 / det) + a * a + b * b;
            if (rr <= 0.0)
                return false;

            double r = System.Math.Sqrt(rr);
            if (r < MinRadius || r > MaxRadius)
                return false;

            // Reject anything that is not actually close to a circle (zig-zag, dog-leg).
            double rms = 0.0;
            for (int i = 0; i < Samples; ++i)
            {
                double u = s_Pos[i].x - mx - a;
                double v = s_Pos[i].z - mz - b;
                double e = System.Math.Sqrt(u * u + v * v) - r;
                rms += e * e;
            }
            rms = System.Math.Sqrt(rms / Samples);
            if (rms > System.Math.Max(3.0, 0.30 * r))
                return false;

            centre = new Vector3((float)(mx + a), s_Pos[0].y, (float)(mz + b));
            radius = (float)r;
            return true;
        }
    }
}
```

---

## 3. 修改 `TankSM.cs`

### 3.1 加常量（放在现有 private 字段附近）

```csharp
// ---- Encirclement (anti-kiting) ----
private const float RingStepDeg = 50f;            // one waypoint step around the ring
private const float InnerCutMargin = 6f;          // how far inside the target's ring we cut
private const float MinRingRadius = 6f;           // >= min turn radius (12 m/s / 180 deg/s = 3.8 m) plus margin
private const float RingMoveHoldSeconds = 1.5f;   // hysteresis: never flip direction faster than this
private const float SwitchHysteresisDeg = 15f;    // dead band around delta_switch
private const float EncircleHandoffRange = 14f;   // close enough to fight -> stop encircling
private const float AllyMinGap = 6f;              // player blast radius is 5 m
private const float FlankSpread = 8f;             // lateral spread when the target is not circling

/// <summary>How this tank closes the angular gap while the target is kiting.</summary>
public enum RingMove
{
    CounterOrbit,   // travel against the target's rotation -> guaranteed head-on meeting
    InnerCut        // travel with it on a smaller radius -> higher angular rate, catches from behind
}

private RingMove m_RingMove = RingMove.InnerCut;
private float m_RingMoveUntil = -1f;
```

### 3.2 暴露最大射程

```csharp
/// <summary>Furthest horizontal distance a shell can still reach.</summary>
public float MaxFireRange => m_MaxFireRange;
```

### 3.3 在 `Update()` 里驱动 OrbitTracker

```csharp
else if (GameManager.IsRoundPlaying)
{
    EnsureRole();
    if (!HasTarget())
        SelectTarget();
    TickTargetTracking();
    OrbitTracker.Tick(Target);          // <-- add (only the first tank per frame does work)
    base.Update();
}
else
{
    m_Started = false;
    OrbitTracker.Reset();               // <-- add
    StopAllCoroutines();
}
```

### 3.4 新增环坐标与合围方法（追加到类尾部，`LaunchProjectile` 之前）

```csharp
        // =====================================================================
        //  Encirclement — used while the target is kiting around an obstacle.
        //
        //  Equal-speed pursuit does not converge: heading for where the target IS
        //  collapses the whole platoon into a single file inside its occlusion
        //  shadow. Once OrbitTracker reports a loop we switch to ring coordinates
        //  (angle + radius about the loop centre) where two mechanisms restore
        //  convergence: counter-rotation, which must meet on a closed loop, and
        //  inner-radius cutting, which turns equal linear speed into a strictly
        //  higher angular rate because omega = v / R.
        // =====================================================================

        /// <summary>Bearing of a world position around the loop centre, in degrees.</summary>
        public float RingAngle(Vector3 position)
        {
            Vector3 d = position - OrbitTracker.Pivot;
            d.y = 0f;
            return Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        }

        /// <summary>Distance of a world position from the loop centre.</summary>
        public float RingRadius(Vector3 position)
        {
            Vector3 d = position - OrbitTracker.Pivot;
            d.y = 0f;
            return d.magnitude;
        }

        /// <summary>
        /// How far a position trails the target around the loop, measured against the target's
        /// direction of travel. 0 = directly behind it, 180 = diametrically opposite.
        /// </summary>
        public float LagAngleOf(Vector3 position)
        {
            float delta = (RingAngle(Target.position) - RingAngle(position)) * OrbitTracker.Sign;
            return Mathf.Repeat(delta, 360f);
        }

        /// <summary>Convenience overload for this tank.</summary>
        public float LagAngle() => LagAngleOf(transform.position);

        /// <summary>
        /// Chooses between cutting inside and reversing around the loop.
        /// <para>
        /// Break-even lag is delta = 180*(k-1)/k with k = Rtarget / Rinner: below it the inner
        /// cut arrives first, above it going the other way does. The tank furthest behind is
        /// always forced to reverse and the closest one always cuts inside, so the platoon can
        /// never end up travelling in a single direction and re-forming a queue.
        /// </para>
        /// The decision is held for RingMoveHoldSeconds; flip-flopping at the boundary would be
        /// worse than the queue it replaces.
        /// </summary>
        public RingMove DecideRingMove()
        {
            if (Time.time < m_RingMoveUntil)
                return m_RingMove;

            float targetRadius = Mathf.Max(RingRadius(Target.position), MinRingRadius);
            float innerRadius = Mathf.Max(MinRingRadius, targetRadius - InnerCutMargin);
            float k = Mathf.Max(targetRadius / innerRadius, 1.05f);
            float switchDeg = 180f * (k - 1f) / k;

            float myLag = LagAngle();

            // Rank inside the platoon by lag. Every tank evaluates this with its own pivot and
            // target, but OrbitTracker is shared so the ordering agrees across the platoon.
            int rank = 0;
            int alive = 0;
            var tanks = GameManager.AIPlatoon.Tanks;
            for (var i = 0; i < tanks.Count; ++i)
            {
                GameObject instance = tanks[i].Instance;
                if (instance == null || !instance.activeSelf)
                    continue;

                ++alive;
                if (instance == gameObject)
                    continue;

                float lag = LagAngleOf(instance.transform.position);
                if (lag > myLag || (Mathf.Abs(lag - myLag) < 0.01f && i < m_PlatoonIndex))
                    ++rank;
            }

            RingMove move;
            if (alive >= 2 && rank == 0)
                move = RingMove.CounterOrbit;                   // furthest behind: always reverse
            else if (alive >= 2 && rank == alive - 1)
                move = RingMove.InnerCut;                       // closest behind: always cut inside
            else if (myLag > switchDeg + SwitchHysteresisDeg)
                move = RingMove.CounterOrbit;
            else if (myLag < switchDeg - SwitchHysteresisDeg)
                move = RingMove.InnerCut;
            else
                move = m_RingMove;                              // inside the dead band: keep going

            m_RingMove = move;
            m_RingMoveUntil = Time.time + RingMoveHoldSeconds;
            return move;
        }

        /// <summary>
        /// Next waypoint around the loop. Deliberately only one step ahead: SetDestination takes
        /// the shortest path, so handing the agent the final bearing would let it pick its own
        /// direction of travel and undo the whole scheme. Re-issuing a short step every
        /// TargetNavMeshUpdate is what actually forces the commanded rotation.
        /// </summary>
        public Vector3 RingDestination()
        {
            RingMove move = DecideRingMove();
            float direction = move == RingMove.CounterOrbit ? -OrbitTracker.Sign : OrbitTracker.Sign;

            float targetRadius = Mathf.Max(RingRadius(Target.position), MinRingRadius);
            float wanted = move == RingMove.CounterOrbit
                ? targetRadius
                : Mathf.Max(MinRingRadius, targetRadius - InnerCutMargin);

            float theta = (RingAngle(transform.position) + direction * RingStepDeg) * Mathf.Deg2Rad;
            Vector3 bearing = new Vector3(Mathf.Sin(theta), 0f, Mathf.Cos(theta));

            // The requested radius can fall inside the obstacle; widen until the NavMesh accepts it.
            for (var attempt = 0; attempt < 3; ++attempt)
            {
                Vector3 candidate = OrbitTracker.Pivot + bearing * (wanted + attempt * 3f);
                candidate.y = transform.position.y;
                if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, 5f, NavMesh.AllAreas))
                    return hit.position;
            }

            return Target.position;
        }

        /// <summary>
        /// Encirclement is only for closing the gap. Once we are close enough to shoot properly
        /// we hand back to the normal engagement, otherwise a tank that has just met the player
        /// head-on would keep walking around the ring and away from it.
        /// </summary>
        public bool ShouldEncircle()
        {
            if (!OrbitTracker.IsOrbiting || !HasTarget())
                return false;
            if (HasLineOfSightToTarget() && DistanceToTarget() <= EncircleHandoffRange)
                return false;
            return true;
        }

        /// <summary>
        /// Earliest point on the target's predicted arc that we can actually reach in time,
        /// i.e. the solution of |P(t) - me| / Speed = t. Used when the target is not circling.
        /// </summary>
        public Vector3 InterceptPoint()
        {
            if (!HasTarget())
                return transform.position;

            Vector3 position = Target.position;
            Vector3 velocity = TargetVelocity;
            const float step = 0.25f;

            for (var i = 1; i <= 20; ++i)                        // 5 s horizon
            {
                velocity = Quaternion.Euler(0f, TargetAngularVelocity * step, 0f) * velocity;
                position += velocity * step;
                if (Vector3.Distance(transform.position, position) / Mathf.Max(GameManager.Speed, 0.1f) <= i * step)
                    return position;
            }

            return Target.position;
        }

        /// <summary>
        /// Interception point spread sideways relative to the TARGET's heading, not to our own.
        /// Offsetting relative to our own bearing (as a naive flank does) rotates with us and
        /// therefore degenerates back into a tail chase.
        /// </summary>
        public Vector3 SpreadDestination()
        {
            Vector3 ahead = InterceptPoint();

            Vector3 velocity = TargetVelocity;
            velocity.y = 0f;
            if (velocity.sqrMagnitude < 1f)
                return ahead;

            float side = AssignedRole == Role.LeftFlank ? -1f
                       : AssignedRole == Role.RightFlank ? 1f
                       : 0f;
            Vector3 lateral = Vector3.Cross(Vector3.up, velocity.normalized);
            Vector3 candidate = ahead + lateral * (side * FlankSpread);

            return NavMesh.SamplePosition(candidate, out NavMeshHit hit, 5f, NavMesh.AllAreas)
                ? hit.position
                : ahead;
        }

        /// <summary>
        /// Pushes a destination away from allies. The player's shell has a 5 m blast radius
        /// (ours is 3.33 m), so two tanks sitting together can be hit by a single shot.
        /// </summary>
        public Vector3 SeparateFromAllies(Vector3 destination)
        {
            var tanks = GameManager.AIPlatoon.Tanks;
            for (var i = 0; i < tanks.Count; ++i)
            {
                GameObject instance = tanks[i].Instance;
                if (instance == null || instance == gameObject || !instance.activeSelf)
                    continue;

                Vector3 offset = destination - instance.transform.position;
                offset.y = 0f;
                float gap = offset.magnitude;
                if (gap < AllyMinGap && gap > 0.01f)
                    destination = instance.transform.position + offset / gap * AllyMinGap;
            }

            return NavMesh.SamplePosition(destination, out NavMeshHit hit, 4f, NavMesh.AllAreas)
                ? hit.position
                : destination;
        }

        /// <summary>Destination for this frame, whichever regime we are in.</summary>
        public Vector3 MovementDestination(bool engaging)
        {
            Vector3 destination = ShouldEncircle() ? RingDestination()
                                : engaging ? OrbitPoint()
                                : SpreadDestination();
            return SeparateFromAllies(destination);
        }
```

### 3.5 调试可视化（可选但强烈建议，方便实测时看清楚在干嘛）

加在类尾部。`OnDrawGizmos` 在 Player 构建里会被自动剥离，不影响交付。

```csharp
#if UNITY_EDITOR
        /// <summary>Editor-only visualisation of the encirclement state.</summary>
        private void OnDrawGizmos()
        {
            if (!Application.isPlaying || !OrbitTracker.IsOrbiting || Target == null)
                return;

            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(OrbitTracker.Pivot, 1f);
            UnityEditor.Handles.color = Color.cyan;
            UnityEditor.Handles.DrawWireDisc(OrbitTracker.Pivot, Vector3.up, OrbitTracker.Radius);

            bool counter = m_RingMove == RingMove.CounterOrbit;
            Gizmos.color = counter ? Color.magenta : Color.yellow;   // magenta = reversing
            Gizmos.DrawLine(transform.position + Vector3.up, NavMeshAgent.destination + Vector3.up);
            Gizmos.DrawWireCube(NavMeshAgent.destination + Vector3.up, Vector3.one * 0.8f);

            UnityEditor.Handles.Label(transform.position + Vector3.up * 3f,
                $"{(counter ? "REVERSE" : "INNER")}  lag={LagAngle():F0}");
        }
#endif
```

---

## 4. 修改 `ChaseState.cs`

整个 `Update()` 和 `PursuitPoint()` 替换成：

```csharp
        /// <summary>
        /// Method <c>Update</c> update logic.
        /// </summary>
        public override void Update()
        {
            base.Update();

            if (!m_TankSM.HasTarget())
            {
                m_StateMachine.ChangeState(m_TankSM.m_States.Patrolling);
                return;
            }

            // Engage as soon as a shot is actually possible. The old threshold (1.2x the orbit
            // radius, about 9.6 m) was never reached while being kited, so the tank stayed in
            // Chase for the whole round and never aimed at anything.
            if (m_TankSM.HasLineOfSightToTarget() &&
                m_TankSM.DistanceToTarget() <= m_TankSM.MaxFireRange * 0.8f)
            {
                m_StateMachine.ChangeState(m_TankSM.m_States.Attack);
                return;
            }

            if (Time.time >= m_TankSM.NavMeshUpdateDeadline)
            {
                m_TankSM.NavMeshUpdateDeadline = Time.time + m_TankSM.TargetNavMeshUpdate;
                m_TankSM.NavMeshAgent.SetDestination(m_TankSM.MovementDestination(false));
            }

            // Aim whenever a shot is possible; only fall back to facing the path otherwise.
            if (m_TankSM.HasLineOfSightToTarget() &&
                m_TankSM.DistanceToTarget() <= m_TankSM.MaxFireRange)
            {
                m_TankSM.RotateTowards(m_TankSM.PredictTargetPoint() - m_TankSM.transform.position);
            }
            else if (m_TankSM.NavMeshAgent.velocity.sqrMagnitude > 0.01f)
            {
                m_TankSM.RotateTowards(m_TankSM.NavMeshAgent.velocity);
            }

            m_TankSM.TryFire();
        }
```

删除原来的 `private Vector3 PursuitPoint()` 整个方法（逻辑已经移进 `TankSM.MovementDestination`）。

---

## 5. 修改 `AttackState.cs`

加一个字段并替换 `Enter()` / `Update()`：

```csharp
        private TankSM m_TankSM;        // 已有
        private float m_LastLosTime;    // 新增：最后一次看见目标的时间

        public override void Enter()
        {
            base.Enter();
            m_TankSM.SetStopDistanceToZero();
            m_TankSM.InitOrbitAngle();
            m_LastLosTime = Time.time;
        }

        public override void Update()
        {
            base.Update();

            if (!m_TankSM.HasTarget())
            {
                m_StateMachine.ChangeState(m_TankSM.m_States.Patrolling);
                return;
            }

            float distance = m_TankSM.DistanceToTarget();
            if (m_TankSM.HasLineOfSightToTarget())
                m_LastLosTime = Time.time;

            // Matches the widened Chase -> Attack threshold. The 0.75 s grace stops the two
            // states ping-ponging every time the target flickers behind a rock.
            if (distance > m_TankSM.MaxFireRange * 0.95f || Time.time - m_LastLosTime > 0.75f)
            {
                m_StateMachine.ChangeState(m_TankSM.m_States.Chase);
                return;
            }

            // While encircling, the ring waypoint owns the movement; the cosmetic orbit angle
            // would fight it.
            if (!m_TankSM.ShouldEncircle())
                m_TankSM.AdvanceOrbitAngle();

            if (Time.time >= m_TankSM.NavMeshUpdateDeadline)
            {
                m_TankSM.NavMeshUpdateDeadline = Time.time + m_TankSM.TargetNavMeshUpdate;
                m_TankSM.NavMeshAgent.SetDestination(m_TankSM.MovementDestination(true));
            }

            m_TankSM.RotateTowards(m_TankSM.PredictTargetPoint() - m_TankSM.transform.position);
            m_TankSM.TryFire();
        }
```

---

## 6. 自测清单（Codex 必须逐条确认后再报告完成）

- [ ] `git status` 显示改动只有 `Assets/Scripts/AI/**` 下的 `.cs` 和一个新增的 `.cs` + `.meta`
- [ ] Unity 2022.3.39f1 编译：**0 error / 0 warning**
- [ ] 场景 `MainAI` 进 Play，三辆 AI 正常出生、正常追击
- [ ] 玩家绕中央建筑群转圈约 4 秒后，Scene 视图出现青色的拟合圆（`OnDrawGizmos`）
- [ ] 此时三辆车中**至少一辆标签是 `REVERSE`**（洋红色连线）
- [ ] 玩家继续绕圈，**在一圈之内会迎头撞上一辆 AI**
- [ ] 玩家停下不动约 3 秒后，青色圆消失，AI 恢复正常追击（**不会绕着空气转圈**）
- [ ] 玩家直线逃跑时不触发合围，三辆车横向铺开而不是排成一列

如果任一条不成立，报告实际观察到的现象和你的判断，不要自行加参数硬凑。

---

## 7. 调参入口（实测后再动，不要一次改多个）

| 常量 | 默认 | 调大的效果 |
|---|---|---|
| `OrbitTracker.EnterSweptDeg` | 100 | 更迟才判定为遛狗（更保守，误触发少） |
| `InnerCutMargin` | 6 | 内切更狠、角速度优势更大，但更容易被建筑物卡住 |
| `RingStepDeg` | 50 | 路点更远，转向更平滑但方向控制更弱 |
| `EncircleHandoffRange` | 14 | 更晚交回普通交战，合围持续更久 |
| `RingMoveHoldSeconds` | 1.5 | 角色更稳定但反应更慢 |

**最小转弯半径是硬下限**：车速 12 m/s、`angularSpeed = 180°/s`，`R = v/ω = 12/π ≈ 3.8 m`。
`MinRingRadius` 不要低于 6，否则 Agent 会甩尾、路径抖动。
