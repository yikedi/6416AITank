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
            m_TankSM.ResumeDriving();
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

            // Stop to engage once we are inside the preferred engagement distance.
            if (m_TankSM.DistanceToTarget() <= m_TankSM.OrbitRadius() * 1.2f)
            {
                m_StateMachine.ChangeState(m_TankSM.m_States.Attack);
                return;
            }

            if (Time.time >= m_TankSM.NavMeshUpdateDeadline)
            {
                m_TankSM.NavMeshUpdateDeadline = Time.time + m_TankSM.TargetNavMeshUpdate;
                m_TankSM.NavMeshAgent.SetDestination(PursuitPoint());
            }
        }

        /// <summary>
        /// Method <c>PursuitPoint</c> returns the position this tank should head toward.
        /// </summary>
        private Vector3 PursuitPoint()
        {
            Vector3 toTarget = m_TankSM.Target.position - m_TankSM.transform.position;
            toTarget.y = 0f;
            float distance = toTarget.magnitude;
            Vector3 direction = distance > 0.01f ? toTarget / distance : m_TankSM.transform.forward;

            // The pusher closes the distance directly.
            if (m_TankSM.AssignedRole == TankSM.Role.Pusher)
                return m_TankSM.Target.position;

            // Flankers approach a point offset to one side of the target.
            float offsetAngle = m_TankSM.AssignedRole == TankSM.Role.LeftFlank ? -55f : 55f;
            Vector3 flankDirection = Quaternion.Euler(0f, offsetAngle, 0f) * direction;
            return m_TankSM.Target.position + flankDirection * m_TankSM.OrbitRadius();
        }
    }
}
