using System.Collections;
using UnityEngine;

using Random = UnityEngine.Random;

namespace CE6127.Tanks.AI
{
    /// <summary>
    /// Class <c>PatrollingState</c> represents the state of the tank when it is searching
    /// for the player. It wanders the map until a target comes within acquisition range.
    /// </summary>
    internal class PatrollingState : BaseState
    {
        private TankSM m_TankSM;           // Reference to the tank state machine.
        private Vector3 m_Destination;     // Destination for the tank to move to.
        private Coroutine m_PatrolRoutine; // Handle to the running patrol coroutine.

        /// <summary>
        /// Constructor <c>PatrollingState</c> constructor.
        /// </summary>
        public PatrollingState(TankSM tankStateMachine) : base("Patrolling", tankStateMachine) => m_TankSM = (TankSM)m_StateMachine;

        /// <summary>
        /// Method <c>Enter</c> on enter.
        /// </summary>
        public override void Enter()
        {
            base.Enter();
            m_TankSM.SetStopDistanceToZero();
            m_PatrolRoutine = m_TankSM.StartCoroutine(Patrolling());
        }

        /// <summary>
        /// Method <c>Update</c> update logic.
        /// </summary>
        public override void Update()
        {
            base.Update();

            // The player's position is always known, so pursue as soon as a target exists.
            if (m_TankSM.HasTarget())
            {
                m_StateMachine.ChangeState(m_TankSM.m_States.Chase);
                return;
            }

            if (Time.time >= m_TankSM.NavMeshUpdateDeadline)
            {
                m_TankSM.NavMeshUpdateDeadline = Time.time + m_TankSM.PatrolNavMeshUpdate;
                m_TankSM.NavMeshAgent.SetDestination(m_Destination);
            }

            // Face the direction of travel while searching.
            if (m_TankSM.NavMeshAgent.velocity.sqrMagnitude > 0.01f)
                m_TankSM.RotateTowards(m_TankSM.NavMeshAgent.velocity);
        }

        /// <summary>
        /// Method <c>Exit</c> on exiting PatrollingState.
        /// </summary>
        public override void Exit()
        {
            base.Exit();

            if (m_PatrolRoutine != null)
                m_TankSM.StopCoroutine(m_PatrolRoutine);
            m_PatrolRoutine = null;
        }

        /// <summary>
        /// Coroutine <c>Patrolling</c> patrolling coroutine.
        /// </summary>
        IEnumerator Patrolling()
        {
            while (true)
            {
                var destination = Random.insideUnitCircle * Random.Range(m_TankSM.PatrolMaxDist.x, m_TankSM.PatrolMaxDist.y);
                m_Destination = m_TankSM.transform.position + new Vector3(destination.x, 0f, destination.y);

                float waitInSec = Random.Range(m_TankSM.PatrolWaitTime.x, m_TankSM.PatrolWaitTime.y);
                yield return new WaitForSeconds(waitInSec);
            }
        }
    }
}
