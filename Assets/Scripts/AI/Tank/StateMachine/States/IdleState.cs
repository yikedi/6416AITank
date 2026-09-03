using UnityEngine;

namespace CE6127.Tanks.AI
{
    /// <summary>
    /// Class <c>IdleState</c> represents the initial state of the tank. It orients the hull
    /// toward the target and hands off to <see cref="ChaseState"/> or <see cref="PatrollingState"/>.
    /// </summary>
    internal class IdleState : BaseState
    {
        private TankSM m_TankSM; // Reference to the tank state machine.

        /// <summary>
        /// Constructor <c>IdleState</c> is the constructor of the class.
        /// </summary>
        public IdleState(TankSM tankStateMachine) : base("Idle", tankStateMachine) => m_TankSM = (TankSM)m_StateMachine;

        /// <summary>
        /// Method <c>Update</c> is called each frame.
        /// </summary>
        public override void Update()
        {
            base.Update();

            if (m_TankSM.HasTarget())
                m_TankSM.RotateTowards(m_TankSM.Target.position - m_TankSM.transform.position);

            // The player's position is always known, so head straight into pursuit.
            if (!m_TankSM.HasTarget())
                m_StateMachine.ChangeState(m_TankSM.m_States.Patrolling);
            else
                m_StateMachine.ChangeState(m_TankSM.m_States.Chase);
        }
    }
}
