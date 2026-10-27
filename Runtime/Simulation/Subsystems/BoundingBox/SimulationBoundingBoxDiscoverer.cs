using System;
using System.Collections.Generic;
using Unity.Collections;
using System.Threading;
// FDT patch: distinguish replacement environment objects from an ordinary session reset.
using UnityEngine.SceneManagement;
using UnityEngine.XR.ARSubsystems;
using static UnityEngine.XR.Simulation.SimulationUtils;

namespace UnityEngine.XR.Simulation
{
    /// <summary>
    /// Helper class for <see cref="SimulationBoundingBoxSubsystem"/> which finds and manages
    /// the bounding boxes in the simulation environment, and fires events correspondingly.
    /// </summary>
    class SimulationBoundingBoxDiscoverer
    {
        struct SimulatedBoundingBoxState
        {
            internal Vector3 position;
            internal Quaternion rotation;
            internal Vector3 size;
            internal TrackingState trackingState;
            internal BoundingBoxClassifications classifications;
        }

        public bool isInitialized => m_Added != null && m_Updated != null && m_Removed != null;

        readonly List<SimulatedBoundingBox> m_BoundingBoxesInScene = new();
        readonly List<SimulatedBoundingBoxState> m_BoundingBoxesStates = new();

        float m_TrackingUpdateIntervalSeconds = 0.1f;
        bool m_Initialized;
        bool m_IsRunning;

        XRBoundingBox[] m_Added;
        int m_NumAdded;
        Dictionary<TrackableId, XRBoundingBox> m_Updated;
        XRBoundingBox[] m_UpdatedCopyBuffer;
        TrackableId[] m_Removed;
        int m_NumRemoved;

        readonly Dictionary<TrackableId, XRBoundingBox> m_AllBoundingBoxes = new();

        CancellationTokenSource m_UpdateCancellationTokenSource;

        // FDT patch: scene identity and delayed restart generation survive Unity component destruction.
        Scene m_EnvironmentScene;
        int m_UpdateGeneration;

        public void Start()
        {
            // FDT patch: a previously initialized, stopped provider can miss the session's environment-reset event.
            if (m_Initialized && m_EnvironmentScene != SimulationSessionSubsystem.simulationSceneManager.environmentScene)
            {
                PrepareForEnvironmentChange();
                Initialize();
                return;
            }
            if (!m_Initialized)
            {
                BaseSimulationSceneManager.environmentSetupFinished += Initialize;
                // FDT patch: the event invokes immediately before adding when the environment already exists.
                if (m_Initialized)
                    BaseSimulationSceneManager.environmentSetupFinished -= Initialize;
                return;
            }

            BeginUpdateLoop();
        }

        public void Stop()
        {
            m_UpdateGeneration++; // FDT patch: invalidate pending delayed restarts too.
            BaseSimulationSceneManager.environmentSetupFinished -= Initialize;

            m_NumAdded = 0;
            // FDT patch: queued replacement removals remain owned until GetChanges consumes them.

            if (!m_IsRunning)
                return;

            m_UpdateCancellationTokenSource?.Cancel();

            // FDT patch: old queued removals can coexist with this environment's final removals.
            var capacity = m_NumRemoved + m_BoundingBoxesInScene.Count;
            if (m_Removed.Length < capacity)
                Array.Resize(ref m_Removed, capacity);

            for (var i = 0; i < m_BoundingBoxesInScene.Count; i++)
            {
                SubsystemRemoveBoundingBoxAtIndex(i);
            }

            m_IsRunning = false;
        }

        async void BeginUpdateLoop()
        {
            m_IsRunning = true;
            using (m_UpdateCancellationTokenSource = new CancellationTokenSource())
            {
                var updateAwaitable = UpdateTracking(m_UpdateCancellationTokenSource.Token);
                await RunWithoutCancellationExceptions(updateAwaitable);
            }
        }

        public void Restart()
        {
            if (!m_Initialized)
                return;

            // FDT patch: a replacement scene requires new objects and removals for the old delivered value identities.
            if (m_EnvironmentScene != SimulationSessionSubsystem.simulationSceneManager.environmentScene)
            {
                PrepareForEnvironmentChange();
                Initialize();
                return;
            }

            if (m_IsRunning)
                Stop();

            Reset();

            for (var i = 0; i < m_BoundingBoxesStates.Count; i++)
            {
                m_BoundingBoxesStates[i] = new SimulatedBoundingBoxState()
                {
                    trackingState = TrackingState.None
                };
            }

            BeginUpdateLoopAfterDelay();
        }

        public void Destroy()
        {
            m_AllBoundingBoxes.Clear();
        }

        void Reset()
        {
            ValidateChanges();
            m_NumAdded = 0;
            Array.Clear(m_Added, 0, m_Added.Length);
            m_Updated.Clear();
            m_AllBoundingBoxes.Clear();
        }

        /// <summary>
        /// Necessary to allow the ARManager-side to catch up to the removed trackables,
        /// otherwise we could end up with a duplicate guid entry and an exception will be thrown.
        /// </summary>
        async void BeginUpdateLoopAfterDelay()
        {
            var generation = m_UpdateGeneration; // FDT patch: keep the existing grace tied to one discovery lifetime.
            await RunWithoutCancellationExceptions(
                Awaitable.WaitForSecondsAsync(m_TrackingUpdateIntervalSeconds, CancellationToken.None));

            if (generation == m_UpdateGeneration && !m_IsRunning)
                BeginUpdateLoop();
        }

        void Initialize()
        {
            // FDT patch: Reset must capture old value IDs before replacement setup can overwrite the object lists.
            BaseSimulationSceneManager.environmentSetupFinished -= Initialize;
            m_UpdateGeneration++; // FDT patch: replacement invalidates a previously queued restart.
            if (m_IsRunning)
                Stop();

            m_BoundingBoxesInScene.Clear();
            m_BoundingBoxesStates.Clear();

            var sceneManager = SimulationSessionSubsystem.simulationSceneManager;
            var simScene = SimulationSessionSubsystem.simulationSceneManager.environmentScene;

            if (!simScene.IsValid())
                throw new InvalidOperationException("The scene loaded for simulation is not valid.");

            var simPhysicsScene = simScene.GetPhysicsScene();

            if (!simPhysicsScene.IsValid())
                throw new InvalidOperationException("The physics scene loaded for simulation is not valid.");

            m_EnvironmentScene = simScene; // FDT patch: retain the environment incarnation used by these objects.

            m_TrackingUpdateIntervalSeconds = XRSimulationRuntimeSettings.Instance.boundingBoxDiscoveryParams.trackingUpdateInterval;

            var simulationBoundingBoxes = sceneManager.simulationEnvironmentBoundingBoxes;
            foreach (var box in simulationBoundingBoxes)
            {
                m_BoundingBoxesInScene.Add(box);
                m_BoundingBoxesStates.Add(CalculateStateOfBoundingBox(box, TrackingState.None));
            }

            InitializeWithCount(m_BoundingBoxesInScene.Count);
            m_Initialized = true;
            BeginUpdateLoop();
        }

        void InitializeWithCount(int numBoundingBoxes)
        {
            m_Added = new XRBoundingBox[numBoundingBoxes];
            m_NumAdded = 0;
            m_Updated = new Dictionary<TrackableId, XRBoundingBox>(numBoundingBoxes);
            m_UpdatedCopyBuffer = new XRBoundingBox[numBoundingBoxes];
            // FDT patch: retain queued removals until the persistent AR manager consumes them.
            if (m_Removed == null)
                m_Removed = new TrackableId[numBoundingBoxes];
            else if (m_Removed.Length < numBoundingBoxes)
                Array.Resize(ref m_Removed, numBoundingBoxes);
        }

        // FDT patch: old components may already be destroyed; use retained value IDs, not component properties.
        void PrepareForEnvironmentChange()
        {
            m_UpdateGeneration++;
            BaseSimulationSceneManager.environmentSetupFinished -= Initialize;
            if (m_IsRunning)
                m_UpdateCancellationTokenSource?.Cancel();
            m_IsRunning = false;
            var capacity = m_NumRemoved + m_AllBoundingBoxes.Count;
            if (m_Removed.Length < capacity)
                Array.Resize(ref m_Removed, capacity);
            foreach (var pair in m_AllBoundingBoxes)
            {
                var pending = false;
                for (var i = 0; i < m_NumAdded; i++)
                    if (m_Added[i].trackableId == pair.Key)
                    {
                        pending = true;
                        break;
                    }
                if (!pending)
                    AppendRemoval(pair.Key);
            }
            m_NumAdded = 0;
            m_Updated.Clear();
            m_AllBoundingBoxes.Clear();
        }

        async Awaitable UpdateTracking(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!m_Initialized)
                {
                    await RunWithoutCancellationExceptions(
                        Awaitable.WaitForSecondsAsync(m_TrackingUpdateIntervalSeconds, cancellationToken));
                    continue;
                }

                using (new ScopedProfiler("XRBoundingBoxTracking"))
                {
                    for (var i = 0; i < m_BoundingBoxesInScene.Count; i++)
                    {
                        var boundingBox = m_BoundingBoxesInScene[i];
                        var prevTrackingState = m_BoundingBoxesStates[i];
                        var newBoundingBoxState = CalculateStateOfBoundingBox(boundingBox, TrackingState.Tracking);

                        if (prevTrackingState.trackingState is TrackingState.None)
                            SubsystemAddBoundingBoxAtIndex(i, newBoundingBoxState);
                        else if (!DoesStateMatch(boundingBox, prevTrackingState))
                        {
                            SubsystemUpdateBoundingBoxAtIndex(i, newBoundingBoxState);
                        }
                    }
                }

                await RunWithoutCancellationExceptions(
                    Awaitable.WaitForSecondsAsync(m_TrackingUpdateIntervalSeconds, cancellationToken));
            }
        }

        static SimulatedBoundingBoxState CalculateStateOfBoundingBox(
            SimulatedBoundingBox boundingBox,
            TrackingState trackingState)
        {
            var pose = boundingBox.GetWorldSpaceCenterPose();
            var state = new SimulatedBoundingBoxState();
            state.position = pose.position;
            state.rotation = pose.rotation;
            state.size = boundingBox.GetWorldSpaceSize();
            state.trackingState = trackingState;
            state.classifications = boundingBox.classifications;
            return state;
        }

        static bool DoesStateMatch(SimulatedBoundingBox boundingBox, SimulatedBoundingBoxState state)
        {
            var pose = boundingBox.GetWorldSpaceCenterPose();
            if (state.position != pose.position)
                return false;

            if (state.rotation != pose.rotation)
                return false;

            if (state.size != boundingBox.GetWorldSpaceSize())
                return false;

            if (state.classifications != boundingBox.classifications)
                return false;

            return true;
        }

        void SubsystemAddBoundingBoxAtIndex(int boxIndex, SimulatedBoundingBoxState simulatedBoundingBoxState)
        {
            var box = m_BoundingBoxesInScene[boxIndex];
            m_BoundingBoxesStates[boxIndex] = simulatedBoundingBoxState;
            var boundingBox = CreateXRBoundingBox(box, simulatedBoundingBoxState.trackingState);
            m_Added[m_NumAdded++] = boundingBox;
            m_AllBoundingBoxes.Add(boundingBox.trackableId, boundingBox);
        }

        void SubsystemUpdateBoundingBoxAtIndex(int boxIndex, SimulatedBoundingBoxState simulatedBoundingBoxState)
        {
            var box = m_BoundingBoxesInScene[boxIndex];
            m_BoundingBoxesStates[boxIndex] = simulatedBoundingBoxState;
            var boundingBox = CreateXRBoundingBox(box, simulatedBoundingBoxState.trackingState);
            m_Updated[boundingBox.trackableId] = boundingBox;
            m_AllBoundingBoxes[boundingBox.trackableId] = boundingBox;
        }

        void SubsystemRemoveBoundingBoxAtIndex(int boxIndex)
        {
            var boundingBox = m_BoundingBoxesInScene[boxIndex];
            // FDT patch: a Single load can destroy the component before its provider receives the scene reset.
            if (boundingBox == null)
                return;
            AppendRemoval(boundingBox.trackableId); // FDT patch: do not deliver the same queued removal twice.
            m_AllBoundingBoxes.Remove(boundingBox.trackableId);
        }

        // FDT patch: rapid Stop/Start or scene replacement must not duplicate an undelivered removal.
        void AppendRemoval(TrackableId id)
        {
            for (var i = 0; i < m_NumRemoved; i++)
                if (m_Removed[i] == id)
                    return;
            m_Removed[m_NumRemoved++] = id;
        }

        static XRBoundingBox CreateXRBoundingBox(SimulatedBoundingBox boundingBox, TrackingState boundingBoxState)
        {
            return new XRBoundingBox(
                trackableId: boundingBox.trackableId,
                pose: boundingBox.GetWorldSpaceCenterPose(),
                size: boundingBox.GetWorldSpaceSize(),
                trackingState: boundingBoxState,
                classifications: boundingBox.classifications,
                nativePtr: IntPtr.Zero);
        }

        /// <summary>
        /// If the same <c>TrackableId</c> is present in more than one list per frame, the
        /// <see cref="ValidationUtility{T}"/> will throw an exception in Editor and development builds.
        /// Because subsystem tracking doesn't update every frame, it's possible that the same trackable has
        /// appeared in multiple lists. Use the most recent update as an add if this happens.
        /// </summary>
        void ValidateChanges()
        {
            for (var i = 0; i < m_NumAdded; i++)
            {
                var trackableId = m_Added[i].trackableId;
                if (!m_Updated.TryGetValue(trackableId, out var latestUpdatedBoundingBox))
                    continue;

                m_Added[i] = latestUpdatedBoundingBox;
                m_Updated.Remove(trackableId);
            }
        }

        public TrackableChanges<XRBoundingBox> GetChanges(
            XRBoundingBox defaultXRBoundingBox,
            Allocator allocator)
        {
            if (!isInitialized)
                return new TrackableChanges<XRBoundingBox>(0, 0, 0, allocator);

            ValidateChanges();

            var numUpdated = m_Updated.Count;

            var changes = new TrackableChanges<XRBoundingBox>(
                m_NumAdded,
                numUpdated,
                m_NumRemoved,
                allocator,
                defaultXRBoundingBox);

            if (m_NumAdded > 0)
            {
                NativeArray<XRBoundingBox>.Copy(m_Added, 0, changes.added, 0, m_NumAdded);
                m_NumAdded = 0;
            }

            if (numUpdated > 0)
            {
                m_Updated.Values.CopyTo(m_UpdatedCopyBuffer, 0);
                NativeArray<XRBoundingBox>.Copy(m_UpdatedCopyBuffer, 0, changes.updated, 0, m_Updated.Count);
                m_Updated.Clear();
            }

            if (m_NumRemoved > 0)
            {
                NativeArray<TrackableId>.Copy(m_Removed, 0, changes.removed, 0, m_NumRemoved);
                m_NumRemoved = 0;
            }

            return changes;
        }

        public IReadOnlyCollection<XRBoundingBox> GetBoundingBoxes()
        {
            return m_AllBoundingBoxes.Values;
        }
    }
}
