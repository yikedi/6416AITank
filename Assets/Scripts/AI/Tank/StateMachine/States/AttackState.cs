using UnityEngine;

namespace CE6127.Tanks.AI
{
    /// <summary>
    /// Class <c>AttackState</c> represents the state of the tank when it is engaging the player.
    /// <para>
    /// The tank stops in place, turns its hull onto the predicted (led) aim point, and fires
    /// while stationary. It hands back to <see cref="ChaseState"/> whenever the target moves
    /// out of range or out of sight, so the vehicle only ever shoots from a standstill.
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
            m_TankSM.StopToAim();
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

            // Reposition (drive again) if the target slipped out of range or out of sight.
            if (distance > m_TankSM.OrbitRadius() * 2.2f ||
                (!hasLos && distance > m_TankSM.OrbitRadius() * 1.5f))
            {
                m_StateMachine.ChangeState(m_TankSM.m_States.Chase);
                return;
            }

            // Standing fire: turn the hull onto the predicted lead point and shoot. The tank is
            // already stopped (StopToAim on enter) and only rotates in place, never drifts.
            Vector3 lead = m_TankSM.PredictTargetPoint();
            m_TankSM.RotateTowards(lead - m_TankSM.transform.position);
            m_TankSM.TryFire();
        }
    }
}
