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
        private TankSM m_TankSM; // Reference to the tank state machine.

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
            bool hasLos = m_TankSM.HasLineOfSightToTarget();

            // Reposition if the target slipped out of range or out of sight.
            if (distance > m_TankSM.OrbitRadius() * 2.2f ||
                (!hasLos && distance > m_TankSM.OrbitRadius() * 1.5f))
            {
                m_StateMachine.ChangeState(m_TankSM.m_States.Chase);
                return;
            }

            // Circle the target to keep distance and throw off return fire.
            m_TankSM.AdvanceOrbitAngle();

            if (Time.time >= m_TankSM.NavMeshUpdateDeadline)
            {
                m_TankSM.NavMeshUpdateDeadline = Time.time + m_TankSM.TargetNavMeshUpdate;
                m_TankSM.NavMeshAgent.SetDestination(m_TankSM.OrbitPoint());
            }

            // Aim at the predicted target position and fire when ready.
            Vector3 lead = m_TankSM.PredictTargetPoint();
            m_TankSM.RotateTowards(lead - m_TankSM.transform.position);
            m_TankSM.TryFire();
        }
    }
}
