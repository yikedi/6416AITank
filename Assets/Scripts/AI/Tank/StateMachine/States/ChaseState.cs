using UnityEngine;

namespace CE6127.Tanks.AI
{
    /// <summary>
    /// Class <c>ChaseState</c> represents the state of the tank when it is closing in on the
    /// player. The exact destination depends on the tank's <see cref="TankSM.AssignedRole"/>:
    /// the pusher heads straight at the target while flankers swing wide to surround it.
    /// </summary>
    internal class ChaseState : BaseState
    {
        private TankSM m_TankSM; // Reference to the tank state machine.

        /// <summary>
        /// Constructor <c>ChaseState</c> constructor.
        /// </summary>
        public ChaseState(TankSM tankStateMachine) : base("Chase", tankStateMachine) => m_TankSM = (TankSM)m_StateMachine;

        /// <summary>
        /// Method <c>Enter</c> on enter.
        /// </summary>
        public override void Enter()
        {
            base.Enter();
            m_TankSM.SetStopDistanceToZero();
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

            // Enter Attack as soon as a shot is genuinely possible; the old orbit-radius gate
            // was never reached while the player kept the platoon behind cover.
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

            // Aim whenever a shot is possible; otherwise face the current path while closing.
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
    }
}
