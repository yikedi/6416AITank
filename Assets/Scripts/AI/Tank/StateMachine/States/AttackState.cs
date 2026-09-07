using UnityEngine;

namespace CE6127.Tanks.AI
{
    /// <summary>
    /// Class <c>AttackState</c> represents the state of the tank when it is engaging the player.
    /// <para>
    /// The tank orbits the target to keep its distance and stay evasive, aims at the predicted
    /// (led) target position, and fires whenever a clean shot is available.
    /// </para>
    /// </summary>
    internal class AttackState : BaseState
    {
        private TankSM m_TankSM;     // Reference to the tank state machine.
        private float m_LastLosTime; // Last time the player was visible, used to debounce cover flicker.

        /// <summary>
        /// Constructor <c>AttackState</c> constructor.
        /// </summary>
        public AttackState(TankSM tankStateMachine) : base("Attack", tankStateMachine) => m_TankSM = (TankSM)m_StateMachine;

        /// <summary>
        /// Method <c>Enter</c> on enter.
        /// </summary>
        public override void Enter()
        {
            base.Enter();
            m_TankSM.SetStopDistanceToZero();
            m_TankSM.InitOrbitAngle();
            m_LastLosTime = Time.time;
        }

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

            float distance = m_TankSM.DistanceToTarget();
            if (m_TankSM.HasLineOfSightToTarget())
                m_LastLosTime = Time.time;

            // Match Chase's wider entry gate. The grace period prevents state ping-pong when
            // the player briefly flickers behind a rock.
            if (distance > m_TankSM.MaxFireRange * 0.95f || Time.time - m_LastLosTime > 0.75f)
            {
                m_StateMachine.ChangeState(m_TankSM.m_States.Chase);
                return;
            }

            // A ring waypoint owns movement during encirclement; normal orbiting resumes after
            // the handoff into close combat.
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
    }
}
