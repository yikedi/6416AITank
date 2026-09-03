using UnityEngine;
using UnityEngine.AI;

using Random = UnityEngine.Random;

namespace CE6127.Tanks.AI
{
    /// <summary>
    /// Class <c>TankSM</c> is the finite state machine that drives a single AI tank.
    /// <para>
    /// It combines three permitted AI techniques:
    /// </para>
    /// <list type="bullet">
    /// <item><b>Finite State Machine</b> — discrete states (<see cref="IdleState"/>,
    /// <see cref="PatrollingState"/>, <see cref="ChaseState"/>, <see cref="AttackState"/>)
    /// with explicit transitions.</item>
    /// <item><b>Utility AI</b> — <see cref="SelectTarget"/> scores candidate targets and
    /// <see cref="OrbitRadius"/> adapts behaviour to the tank's remaining health.</item>
    /// <item><b>Platoon roles</b> — <see cref="AssignedRole"/> spreads the platoon around the
    /// player (one presser, two flankers) so they surround and focus-fire the target.</item>
    /// </list>
    /// </summary>
    internal class TankSM : StateMachine
    {
        /// <summary>
        /// Enum <c>Role</c> assigns each tank a position in the platoon's attack formation.
        /// </summary>
        public enum Role
        {
            Pusher,     // Closes the distance head-on and applies the most pressure.
            LeftFlank,  // Approaches and orbits from the target's left side.
            RightFlank  // Approaches and orbits from the target's right side.
        }

        protected internal struct States
        {
            // States:
            public IdleState Idle;
            public PatrollingState Patrolling;
            public ChaseState Chase;
            public AttackState Attack;

            internal States(TankSM sm)
            {
                Idle = new IdleState(sm);
                Patrolling = new PatrollingState(sm);
                Chase = new ChaseState(sm);
                Attack = new AttackState(sm);
            }
        }

        public States m_States;
        [HideInInspector] public GameManager GameManager;           // Reference to the GameManager.
        [HideInInspector] public NavMeshAgent NavMeshAgent;         // Reference to the NavMeshAgent.

        [Header("Patrolling")]
        [Tooltip("Minimum and maximum time delay for patrolling wait.")]
        public Vector2 PatrolWaitTime = new(1.5f, 3.5f);            // A minimum and maximum time delay for patrolling wait.
        [Tooltip("Minimum and maximum circumradius of the area to patrol at a given update time.")]
        public Vector2 PatrolMaxDist = new(15f, 30f);               // A minimum and maximum circumradius of the area to patrol.
        [Range(0f, 2f)] public float PatrolNavMeshUpdate = 0.2f;    // A delay between each patrolling path update.

        [Header("Targeting")]
        [Tooltip("Minimum and maximum range for the targeting range.")]
        public Vector2 StartToTargetDist = new(28f, 35f);           // A minimum and maximum range for the targeting range.
        [HideInInspector] public float TargetDistance;              // The distance between the tank and the target.
        [Tooltip("Minimum and maximum range for the stopping range.")]
        public Vector2 StopAtTargetDist = new(18f, 22f);            // A minimum and maximum range for the stopping range.
        [HideInInspector] public float StopDistance;                // The distance between the tank and the target.
        [Range(0f, 2f)] public float TargetNavMeshUpdate = 0.2f;    // A delay between each targeting path update.

        [Header("Blending")]
        [Range(0f, 1f)] public float OrientSlerpScalar = 0.2f;      // A scalar for the slerp when searching.

        [Header("Engagement")]
        [Tooltip("Range within which the AI will begin pursuing the target.")]
        public float AcquireRange = 40f;                            // How far away a target can be before we start chasing it.
        [Tooltip("Minimum horizontal distance at which the AI will fire. 0 = fire even at point blank (self-damage accepted; kill takes priority over survival).")]
        public float MinFireRange = 0f;                             // Below this distance the AI holds fire (0 = never hold).
        [Tooltip("Base distance the AI tries to keep from the target while engaging.")]
        public float EngageRadius = 8f;                             // How close the AI tries to get while attacking.
        [Tooltip("Maximum aim offset (degrees) tolerated before firing.")]
        public float AimToleranceDeg = 3.5f;                        // How accurately the hull must face the target before firing.
        [Tooltip("How fast the AI orbits the target (degrees per second).")]
        public float OrbitAngularSpeed = 25f;                       // How quickly the AI circles the target while attacking.
        [Tooltip("Radius multiplier applied to flankers so they keep their distance.")]
        public float FlankRadiusMultiplier = 1.25f;                 // Flankers orbit further out than the pusher.

        [HideInInspector] public Transform Target;                  // Reference to the target's transform.
        [HideInInspector] public float NavMeshUpdateDeadline;       // The time when the next path update is due.
        [HideInInspector] public Vector3 TargetVelocity;            // The smoothed velocity of the target (for lead prediction).
        [HideInInspector] public float TargetAngularVelocity;       // The smoothed turn rate of the target (deg/s, for curved lead prediction).
        [HideInInspector] public Role AssignedRole = Role.Pusher;   // This tank's role in the platoon formation.
        [HideInInspector] public int StrafeSign = 1;                // +1 orbits counter-clockwise, -1 clockwise.
        [HideInInspector] public float OrbitAngle;                  // Current angular position of the tank around the target.

        [Header("Firing")]
        [Tooltip("Minimum and maximum cooldown time delay between each firing in seconds.")]
        public Vector2 FireInterval = new(0.7f, 2.5f);              // A minimum and maximum cooldown time delay between each firing.
        [Tooltip("Force given to the shell if the fire button is not held, and the force given to the shell if the fire button is held for the max charge time in seconds.")]
        public Vector2 LaunchForceMinMax = new(6.5f, 30f);          // The force given to the shell if the fire button is not held, and the force given to the shell if the fire button is held for the max charge time.
        [Tooltip("Height at which the shell crosses the target's plane, as a fraction of the launch height. 1 = skims the tank top at barrel height (tends to fly over), 0.7 = drops onto the hull, 0 = lands on the ground.")]
        [Range(0f, 1f)] public float PassHeightFactor = 1.0f;       // Intercept height for MaxForceWithoutOvershoot as a fraction of m_LaunchHeight.

        [Header("References")]
        [Tooltip("Prefab")] public Rigidbody Shell;                 // Prefab of the shell.
        [Tooltip("Transform")] public Transform FireTransform;      // A child of the tank where the shells are spawned.
        [Header("Firing Audio")]
        public AudioSource SFXAudioSource;                          // Reference to the audio source used to play the shooting audio.
        public AudioClip ShotFiringAudioClip;                       // Audio that plays when each shot is fired.

        private bool m_Started = false;                             // Whether the tank has started moving.
        private Rigidbody m_Rigidbody;                              // Reference used to the tank's rigidbody.
        private TankSound m_TankSound;                              // Reference used to play sound effects.
        private TankHealth m_TankHealth;                            // Reference used to read the tank's own health.
        private int m_PlatoonIndex = -1;                            // Index of this tank within the AI platoon (-1 = unassigned).
        private float m_NextFireTime;                               // Earliest time the tank may fire again.
        private float m_BarrelPitchRad;                             // Fixed elevation of the barrel above the horizontal plane.
        private float m_LaunchHeight;                               // Height of the barrel above the tank origin.
        private float m_MaxFireRange;                               // Furthest distance a shell can still reach.
        private Vector3 m_PrevTargetPosition;                       // Used to estimate the target's velocity.
        private Vector3 m_PrevTargetForward;                        // Used to estimate the target's turn rate.
        private bool m_TargetTracked;                               // Whether target velocity tracking has started.

        /// <summary>
        /// Method <c>MoveTurnSound</c> returns the current tank's velocity.
        /// </summary>
        private Vector2 MoveTurnSound() => new Vector2(Mathf.Abs(NavMeshAgent.velocity.x), Mathf.Abs(NavMeshAgent.velocity.z));

        /// <summary>
        /// Method <c>GetInitialState</c> returns the initial state of the state machine.
        /// </summary>
        protected override BaseState GetInitialState() => m_States.Idle;

        /// <summary>
        /// Method <c>SetNavMeshAgent</c> sets the NavMeshAgent's speed and angular speed.
        /// </summary>
        private void SetNavMeshAgent()
        {
            NavMeshAgent.speed = GameManager.Speed;
            NavMeshAgent.angularSpeed = GameManager.AngularSpeed;
            // Driving: the agent rotates the hull onto its heading so the tank only ever
            // moves forward (no sideways drift). See ResumeDriving / StopToAim.
            NavMeshAgent.updateRotation = true;
        }

        /// <summary>
        /// Method <c>SetStopDistanceToZero</c> sets the NavMeshAgent's stopping distance to zero.
        /// </summary>
        public void SetStopDistanceToZero() => NavMeshAgent.stoppingDistance = 0f;

        /// <summary>
        /// Method <c>SetStopDistanceToTarget</c> sets the NavMeshAgent's stopping distance to the target's distance.
        /// </summary>
        public void SetStopDistanceToTarget() => NavMeshAgent.stoppingDistance = StopDistance;

        /// <summary>
        /// Method <c>ResumeDriving</c> puts the tank back in driving mode: the agent steers the
        /// hull to face the direction of travel, so the tank advances head-on and never slides
        /// sideways. Call it whenever the tank should move (patrol, pursuit, repositioning).
        /// </summary>
        public void ResumeDriving()
        {
            NavMeshAgent.updateRotation = true;
            NavMeshAgent.isStopped = false;
        }

        /// <summary>
        /// Method <c>StopToAim</c> halts the tank so it can aim and fire in place: the agent no
        /// longer moves or rotates, and the hull is turned manually onto the aim point. Manual
        /// rotation must only ever happen while the tank is stopped, otherwise the vehicle would
        /// drift sideways as the agent pushes one way while the hull faces another.
        /// </summary>
        public void StopToAim()
        {
            NavMeshAgent.velocity = Vector3.zero;
            NavMeshAgent.isStopped = true;
            NavMeshAgent.updateRotation = false;
        }

        /// <summary>
        /// Method <c>Awake</c> is called when the script instance is being loaded.
        /// </summary>
        private void Awake()
        {
            m_States = new States(this);

            GameManager = GameManager.Instance;

            m_Rigidbody = GetComponent<Rigidbody>();
            NavMeshAgent = GetComponent<NavMeshAgent>();
            m_TankSound = GetComponent<TankSound>();
            m_TankHealth = GetComponent<TankHealth>();

            SetNavMeshAgent();

            TargetDistance = Random.Range(StartToTargetDist.x, StartToTargetDist.y);
            StopDistance = Random.Range(StopAtTargetDist.x, StopAtTargetDist.y);

            SetStopDistanceToTarget();

            // Cache the fixed barrel geometry used by the ballistic firing solution.
            m_BarrelPitchRad = Mathf.Asin(Mathf.Clamp(FireTransform.forward.y, -1f, 1f));
            m_LaunchHeight = FireTransform.position.y - transform.position.y;
            m_MaxFireRange = ComputeMaxFireRange();

            SelectTarget();

            m_NextFireTime = Time.time;
        }

        /// <summary>
        /// Method <c>OnEnable</c> is called when the object becomes enabled and active.
        /// </summary>
        private void OnEnable()
        {
            // When the tank is turned on, make sure it's not kinematic.
            m_Rigidbody.isKinematic = false;
        }

        /// <summary>
        /// Method <c>Start</c> is called on the frame when a script is enabled just before any of the Update methods are called the first time.
        /// </summary>
        private new void Start()
        {
            m_TankSound.MoveTurnInputCalc += MoveTurnSound;
        }

        /// <summary>
        /// Method <c>OnDisable</c> is called when the behaviour becomes disabled or inactive.
        /// </summary>
        private void OnDisable()
        {
            // When the tank is turned off, set it to kinematic so it stops moving.
            m_Rigidbody.isKinematic = true;

            m_TankSound.MoveTurnInputCalc -= MoveTurnSound;
        }

        /// <summary>
        /// Method <c>Update</c> is called every frame, if the MonoBehaviour is enabled.
        /// </summary>
        private new void Update()
        {
            if (!m_Started && GameManager.IsRoundPlaying)
            {
                m_Started = true;
                EnsureRole();
                SelectTarget();
                m_NextFireTime = Time.time;
                base.Start();
            }
            else if (GameManager.IsRoundPlaying)
            {
                EnsureRole();
                if (!HasTarget())
                    SelectTarget();
                TickTargetTracking();
                base.Update();
            }
            else
            {
                m_Started = false;
                StopAllCoroutines();
            }
        }

        /// <summary>
        /// Method <c>EnsureRole</c> assigns this tank a platoon role based on its index.
        /// </summary>
        private void EnsureRole()
        {
            if (m_PlatoonIndex >= 0)
                return;

            var tanks = GameManager.AIPlatoon.Tanks;
            for (var i = 0; i < tanks.Count; ++i)
            {
                if (tanks[i].Instance == gameObject)
                {
                    m_PlatoonIndex = i;
                    break;
                }
            }

            if (m_PlatoonIndex < 0)
                m_PlatoonIndex = 0;

            AssignedRole = (Role)(m_PlatoonIndex % 3);
            StrafeSign = AssignedRole == Role.LeftFlank ? -1 : 1;
        }

        /// <summary>
        /// Method <c>SelectTarget</c> picks the player tank with the highest utility.
        /// <para>
        /// The score is currently the straight-line distance, so the whole platoon concentrates
        /// its fire on the closest living player tank. Extra considerations (line of sight,
        /// low-health targets) can be folded into <c>score</c> without touching the FSM.
        /// </para>
        /// </summary>
        public void SelectTarget()
        {
            Transform best = null;
            float bestScore = float.MaxValue;

            foreach (var tank in GameManager.PlayerPlatoon.Tanks)
            {
                if (tank.Instance == null || !tank.Instance.activeSelf)
                    continue;

                Transform candidate = tank.Instance.transform;
                float score = Vector3.Distance(transform.position, candidate.position);

                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            Target = best;
            m_TargetTracked = false;
        }

        /// <summary>
        /// Method <c>HasTarget</c> returns whether a live target is currently selected.
        /// </summary>
        public bool HasTarget() => Target != null && Target.gameObject.activeSelf;

        /// <summary>
        /// Method <c>DistanceToTarget</c> returns the horizontal distance to the target.
        /// </summary>
        public float DistanceToTarget()
        {
            if (!HasTarget())
                return float.MaxValue;

            Vector3 delta = transform.position - Target.position;
            delta.y = 0f;
            return delta.magnitude;
        }

        /// <summary>
        /// Method <c>TickTargetTracking</c> estimates the target's linear and angular velocity
        /// for lead prediction. Both are frame-differenced and smoothed, with the turn rate
        /// clamped to the target's maximum (180°/s).
        /// </summary>
        public void TickTargetTracking()
        {
            if (!HasTarget())
            {
                TargetVelocity = Vector3.zero;
                TargetAngularVelocity = 0f;
                m_TargetTracked = false;
                return;
            }

            // Horizontal forward direction of the target, used to estimate its turn rate.
            Vector3 forward = Target.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
                forward = transform.forward;
            forward.Normalize();

            if (!m_TargetTracked)
            {
                m_PrevTargetPosition = Target.position;
                m_PrevTargetForward = forward;
                TargetVelocity = Vector3.zero;
                TargetAngularVelocity = 0f;
                m_TargetTracked = true;
                return;
            }

            // Linear velocity: frame-differenced position, smoothed.
            Vector3 instant = (Target.position - m_PrevTargetPosition) / Mathf.Max(Time.deltaTime, 1e-4f);
            TargetVelocity = Vector3.Lerp(TargetVelocity, instant, 0.4f);
            m_PrevTargetPosition = Target.position;

            // Angular velocity: signed angle between consecutive forward directions (deg/s).
            float angleDeg = Vector3.SignedAngle(m_PrevTargetForward, forward, Vector3.up);
            float instantAngular = angleDeg / Mathf.Max(Time.deltaTime, 1e-4f);
            TargetAngularVelocity = Mathf.Clamp(Mathf.Lerp(TargetAngularVelocity, instantAngular, 0.4f), -180f, 180f);
            m_PrevTargetForward = forward;
        }

        /// <summary>
        /// Method <c>HasLineOfSight</c> returns whether the given world point is not occluded by a wall.
        /// </summary>
        public bool HasLineOfSight(Vector3 point)
        {
            Vector3 origin = FireTransform.position;
            if (Physics.Linecast(origin, point, out RaycastHit hit))
                return hit.collider.GetComponentInParent<TankHealth>() != null; // A tank is not an obstacle.
            return true;
        }

        /// <summary>
        /// Method <c>HasLineOfSightToTarget</c> returns whether the target's body is visible.
        /// </summary>
        public bool HasLineOfSightToTarget()
        {
            if (!HasTarget())
                return false;
            return HasLineOfSight(Target.position + Vector3.up * 0.85f);
        }

        /// <summary>
        /// Method <c>ComputeMaxFireRange</c> solves for the furthest horizontal distance a shell can reach.
        /// </summary>
        private float ComputeMaxFireRange()
        {
            float g = Mathf.Max(Physics.gravity.magnitude, 0.01f);
            float cos = Mathf.Cos(m_BarrelPitchRad);
            float sin = Mathf.Sin(m_BarrelPitchRad);
            float vMax = LaunchForceMinMax.y;

            // g*d^2 - 2*v^2*cos*sin*d - 2*v^2*cos^2*h = 0
            float a = g;
            float b = -2f * vMax * vMax * cos * sin;
            float c = -2f * vMax * vMax * cos * cos * m_LaunchHeight;
            float disc = b * b - 4f * a * c;

            if (disc <= 0f)
                return LaunchForceMinMax.y;

            return (-b + Mathf.Sqrt(disc)) / (2f * a);
        }

        /// <summary>
        /// Method <c>LaunchForceForDistance</c> returns the shell speed that lands the projectile
        /// at the given horizontal distance, given the fixed barrel elevation and gravity.
        /// </summary>
        public float LaunchForceForDistance(float horizontalDistance)
        {
            float g = Mathf.Max(Physics.gravity.magnitude, 0.01f);
            float cos = Mathf.Cos(m_BarrelPitchRad);
            float tan = Mathf.Tan(m_BarrelPitchRad);

            float denom = 2f * cos * cos * (m_LaunchHeight + horizontalDistance * tan);
            if (denom <= 0f)
                return LaunchForceMinMax.y;

            float vSqr = g * horizontalDistance * horizontalDistance / denom;
            return Mathf.Clamp(Mathf.Sqrt(Mathf.Max(0f, vSqr)), LaunchForceMinMax.x, LaunchForceMinMax.y);
        }

        /// <summary>
        /// Method <c>MaxForceWithoutOvershoot</c> returns the fastest shell speed whose trajectory
        /// crosses the target's vertical plane at <see cref="PassHeightFactor"/> × launch height.
        /// With the factor at 1 the shell returns to barrel height at the target (it skims the roof
        /// and tends to fly over); lowering it makes the shell drop onto the hull instead.
        /// </summary>
        public float MaxForceWithoutOvershoot(float horizontalDistance)
        {
            float g = Mathf.Max(Physics.gravity.magnitude, 0.01f);
            float cos = Mathf.Cos(m_BarrelPitchRad);
            float tan = Mathf.Tan(m_BarrelPitchRad);

            // Vertical drop below the launch line required by the time the shell covers d.
            float drop = (1f - Mathf.Clamp01(PassHeightFactor)) * m_LaunchHeight;

            // y(d) = h + d·tanθ − g·d²/(2·v²·cos²θ) = PassHeightFactor·h  ⇒  solve for v.
            float denom = 2f * cos * cos * (horizontalDistance * tan + drop);
            if (denom <= 0f)
                return LaunchForceMinMax.y;

            float vSqr = g * horizontalDistance * horizontalDistance / denom;
            return Mathf.Clamp(Mathf.Sqrt(Mathf.Max(0f, vSqr)), LaunchForceMinMax.x, LaunchForceMinMax.y);
        }

        /// <summary>
        /// Method <c>FlightTime</c> returns how long a shell fired with
        /// <see cref="MaxForceWithoutOvershoot"/> takes to cover the given horizontal distance.
        /// </summary>
        public float FlightTime(float horizontalDistance)
        {
            float v = MaxForceWithoutOvershoot(horizontalDistance);
            float speed = Mathf.Max(v * Mathf.Cos(m_BarrelPitchRad), 0.1f);
            return horizontalDistance / speed;
        }

        /// <summary>
        /// Method <c>PredictTargetPoint</c> returns the world position to aim at.
        /// <para>
        /// The target's future positions are sampled along its curved path (current linear and
        /// angular velocity) over the next 1.5s at 0.1s steps. Each sample is scored by whether
        /// the shell can arrive exactly when the target does: <c>rotateTime + flightTime ≈ t</c>.
        /// The earliest sample that can be hit on time wins; the closest match is the fallback.
        /// </para>
        /// </summary>
        public Vector3 PredictTargetPoint()
        {
            if (!HasTarget())
                return transform.position + transform.forward * 10f;

            const float horizon = 1.5f;
            const int sampleCount = 40;             // Number of future-path samples over the horizon.
            const float step = horizon / sampleCount;
            const float rotSpeed = 180f;            // NavMeshAgent.angularSpeed (deg/s).
            const float timingTolerance = 0.05f;    // ~0.4m of travel at 8 m/s.

            Vector3 forward = FireTransform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
                forward = transform.forward;
            forward.Normalize();

            // Integrate the target's curved path, checking each sample as we go.
            Vector3 pos = Target.position;
            Vector3 vel = TargetVelocity;

            Vector3 bestAim = Target.position;
            float bestDiff = float.MaxValue;

            for (int i = 1; i <= sampleCount; ++i)
            {
                float t = i * step;

                vel = Quaternion.Euler(0f, TargetAngularVelocity * step, 0f) * vel;
                pos += vel * step;

                Vector3 delta = pos - FireTransform.position;
                delta.y = 0f;
                float d = delta.magnitude;
                if (d < 0.01f)
                    continue;

                float rotateTime = Vector3.Angle(forward, delta) / rotSpeed;
                float flightTime = FlightTime(d);
                float diff = Mathf.Abs(rotateTime + flightTime - t);

                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    bestAim = pos;
                }

                // Earliest sample that can be hit on time.
                if (diff <= timingTolerance)
                    return pos;
            }

            return bestAim;
        }

        /// <summary>
        /// Method <c>RotateTowards</c> rotates the hull on the y-axis toward a world direction.
        /// </summary>
        public void RotateTowards(Vector3 worldDirection)
        {
            worldDirection.y = 0f;
            if (worldDirection.sqrMagnitude < 0.0001f)
                return;

            // Turn rate is hard-capped at the pinned NavMeshAgent angular speed (180°/s).
            Quaternion targetRotation = Quaternion.LookRotation(worldDirection);
            float maxDegrees = NavMeshAgent.angularSpeed * Time.deltaTime;
            transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRotation, maxDegrees);
        }

        /// <summary>
        /// Method <c>OrbitRadius</c> returns the preferred engagement distance for this tank.
        /// <para>
        /// The base distance is <see cref="EngageRadius"/>, so the tank stays close to the target.
        /// Flankers keep further away than the pusher, and a damaged tank hangs back defensively.
        /// </para>
        /// </summary>
        public float OrbitRadius()
        {
            float roleMultiplier = AssignedRole == Role.Pusher ? 1f : FlankRadiusMultiplier;
            float healthMultiplier = HealthFraction() < 0.35f ? 1.4f : 1f;
            return EngageRadius * roleMultiplier * healthMultiplier;
        }

        /// <summary>
        /// Method <c>HealthFraction</c> returns the tank's remaining health as a 0..1 ratio.
        /// </summary>
        private float HealthFraction()
        {
            if (m_TankHealth == null)
                return 1f;
            return Mathf.Clamp01(m_TankHealth.CurrentHealth / Mathf.Max(m_TankHealth.StartingHealth, 1f));
        }

        /// <summary>
        /// Method <c>InitOrbitAngle</c> seeds the orbit angle from the tank's current bearing around the target.
        /// </summary>
        public void InitOrbitAngle()
        {
            if (!HasTarget())
            {
                OrbitAngle = 0f;
                return;
            }

            Vector3 delta = transform.position - Target.position;
            delta.y = 0f;
            OrbitAngle = Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Method <c>AdvanceOrbitAngle</c> advances the orbit angle, causing the tank to circle the target.
        /// </summary>
        public void AdvanceOrbitAngle() => OrbitAngle += OrbitAngularSpeed * Time.deltaTime * StrafeSign;

        /// <summary>
        /// Method <c>OrbitPoint</c> returns the world position on the orbit circle the tank should head toward.
        /// </summary>
        public Vector3 OrbitPoint()
        {
            float phi = OrbitAngle * Mathf.Deg2Rad;
            Vector3 direction = new Vector3(Mathf.Sin(phi), 0f, Mathf.Cos(phi));
            return Target.position + direction * OrbitRadius();
        }

        /// <summary>
        /// Method <c>TryFire</c> fires a shell when the target is in range, visible, and within the aim tolerance.
        /// </summary>
        public bool TryFire()
        {
            if (!HasTarget())
                return false;
            if (Time.time < m_NextFireTime)
                return false;

            // Aim at the led (predicted) target point. The launch force must reach this same
            // point, otherwise the aim direction and shell range disagree (shots fall short or
            // overshoot whenever the target is moving).
            Vector3 aim = PredictTargetPoint() - FireTransform.position;
            aim.y = 0f;
            float aimDistance = aim.magnitude;
            if (aim.sqrMagnitude < 0.01f)
                return false;

            if (aimDistance < MinFireRange || aimDistance > m_MaxFireRange)
                return false;

            Vector3 forward = FireTransform.forward;
            forward.y = 0f;

            // Aim tolerance scales with distance. Below ~5m the shell's body makes a modest
            // misalignment still connect, so widen by an extra 30%; above ~8m tighten for
            // accuracy. Both blend smoothly over the transition band in between.
            float rangeFactor = Mathf.Clamp(8f / Mathf.Max(aimDistance, 0.5f), 1f, 6f);
            float precisionFactor = Mathf.Lerp(1f, 0.7f, Mathf.InverseLerp(8f, 14f, aimDistance));
            float closeFactor = Mathf.Lerp(1.3f, 1f, Mathf.InverseLerp(5f, 8f, aimDistance));
            float tolerance = AimToleranceDeg * rangeFactor * precisionFactor * closeFactor;
            if (Vector3.Angle(forward, aim) > tolerance)
                return false;

            if (!HasLineOfSightToTarget())
                return false;

            LaunchProjectile(MaxForceWithoutOvershoot(aimDistance));
            m_NextFireTime = Time.time + FireInterval.x;
            return true;
        }

        /// <summary>
        /// Method <c>LaunchProjectile</c> instantiate and launch the shell.
        /// </summary>
        public void LaunchProjectile(float launchForce = 1f)
        {
            launchForce = Mathf.Min(Mathf.Max(LaunchForceMinMax.x, launchForce), LaunchForceMinMax.y);

            // Create an instance of the shell and store a reference to it's rigidbody.
            Rigidbody shellInstance = Instantiate(Shell, FireTransform.position, FireTransform.rotation) as Rigidbody;

            // Set the shell's velocity to the launch force in the fire position's forward direction.
            shellInstance.velocity = launchForce * FireTransform.forward;

            // Change the clip to the firing clip and play it.
            SFXAudioSource.clip = ShotFiringAudioClip;
            SFXAudioSource.Play();
        }
    }
}
