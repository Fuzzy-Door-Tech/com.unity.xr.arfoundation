using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.XR.CoreUtils;
// FDT patch: distinguish a replacement environment from a reset within the same scene.
using UnityEngine.SceneManagement;
using UnityEngine.XR.ARSubsystems;
using static UnityEngine.XR.Simulation.SimulationUtils;

namespace UnityEngine.XR.Simulation
{
    /// <summary>
    /// Helper class for <see cref="SimulationImageTrackingSubsystem"/> which simulates a device's discovery of
    /// tracked images in an environment and fires corresponding events.
    /// </summary>
    class SimulationTrackedImageDiscoverer
    {
        int m_TrackingUpdateIntervalMilliseconds = 100;
        int m_PreviousLibraryCount;
        SimulationRuntimeImageLibrary m_SimulationRuntimeImageLibrary;
        TrackedImageDiscoveryStrategy m_ImageDiscoveryStrategy;
        bool m_Initialized;
        bool m_IsRunning;
        CancellationTokenSource m_UpdateCancellationTokenSource;

        readonly List<SimulatedTrackedImage> m_ImagesInScene = new();
        readonly List<TrackingState> m_TrackingStatesOfImages = new();
        readonly List<XRReferenceImage?> m_ReferenceImagesForImages = new();

        // FDT patch: retain value identities after Unity destroys the environment's components.
        struct ImageDelivery
        {
            internal TrackableId id;
            internal bool pendingAdd;
            internal bool delivered;
        }
        readonly List<ImageDelivery> m_ImageDeliveries = new();
        Scene m_EnvironmentScene;
        int m_UpdateGeneration;
        internal bool environmentChanged => m_Initialized &&
            m_EnvironmentScene != SimulationSessionSubsystem.simulationSceneManager.environmentScene;

        internal int CopyDeliveredImageIds(TrackableId[] destination, int offset)
        {
            for (var i = 0; i < m_ImageDeliveries.Count; i++)
            {
                var image = m_ImageDeliveries[i];
                if (image.delivered)
                    destination[offset++] = image.id;
            }
            return offset;
        }

        // FDT patch: only GetChanges delivers an addition; tracking state alone does not establish consumer ownership.
        internal void FinishPendingImageAdditions(bool delivered)
        {
            for (var i = 0; i < m_ImageDeliveries.Count; i++)
            {
                var image = m_ImageDeliveries[i];
                if (!image.pendingAdd)
                    continue;
                image.delivered |= delivered;
                image.pendingAdd = false;
                m_ImageDeliveries[i] = image;
            }
        }

        /// <summary>
        /// This property allows for a class to override the ITrackedImageDiscoveryParams to use
        /// by this discoverer.  The discovery params, if set, will implement the parameters needed
        /// by the discoverer.  If no params are set, then the default behavior of this discoverer
        /// is to use the XRSimulationRuntimeSettings.
        /// </summary>
        internal static ITrackedImageDiscoveryParams trackedImageDiscoveryParams { get; set; }

        public SimulationRuntimeImageLibrary simulationRuntimeImageLibrary
        {
            set => m_SimulationRuntimeImageLibrary = value;
        }

        /// <summary>
        /// Invoked during initialization, after all <see cref="SimulatedTrackedImage"/> components in the
        /// simulation environment have been loaded. Returns the total number of <see cref="SimulatedTrackedImage"/>
        /// components in the simulation environment.
        /// </summary>
        public event Action<int> imagesInitialized;

        /// <summary>
        /// Invoked when a tracked image is discovered.
        /// </summary>
        public event Action<XRTrackedImage> imageAdded;

        /// <summary>
        /// Invoked when a tracked image is updated.
        /// </summary>
        public event Action<XRTrackedImage> imageUpdated;

        /// <summary>
        /// Starts actively trying to discover tracked images in the environment.
        /// </summary>
        public void Start()
        {
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
            // FDT patch: an old delayed restart must not revive discovery after stop/replacement.
            m_UpdateGeneration++;
            BaseSimulationSceneManager.environmentSetupFinished -= Initialize;

            if (!m_IsRunning)
                return;

            m_UpdateCancellationTokenSource?.Cancel();

            // any trackables already not deemed as not tracking, revert to a "limited" tracking
            // status until the discoverer is resumed.
            for (var i = 0; i < m_ImagesInScene.Count; i++)
            {
                if (m_TrackingStatesOfImages[i] != TrackingState.None)
                    m_TrackingStatesOfImages[i] = TrackingState.Limited;
            }

            m_IsRunning = false;
        }

        async void BeginUpdateLoop()
        {
            m_IsRunning = true;
            using (m_UpdateCancellationTokenSource = new CancellationTokenSource())
            {
                var updateTask = UpdateTracking(m_UpdateCancellationTokenSource.Token);
                await RunWithoutCancellationExceptions(updateTask);
            }
        }

        public void Restart()
        {
            if(!m_Initialized)
                return;

            // FDT patch: reset hooks must enumerate replacement objects instead of reusing destroyed references.
            if (environmentChanged)
            {
                Initialize();
                return;
            }

            if(m_IsRunning)
                Stop();

            for (var i = 0; i < m_TrackingStatesOfImages.Count; i++)
            {
                m_TrackingStatesOfImages[i] = TrackingState.None;
            }

            BeginUpdateLoopAfterDelay();
        }

        /// <summary>
        /// Necessary to allow the ARManager-side to catch up to the removed trackables,
        /// otherwise we could end up with a duplicate guid entry and an exception will be thrown.
        /// </summary>
        async void BeginUpdateLoopAfterDelay()
        {
            // FDT patch: keep the existing reset grace, scoped to the discovery incarnation that requested it.
            var generation = m_UpdateGeneration;
            await RunWithoutCancellationExceptions(Task.Delay(m_TrackingUpdateIntervalMilliseconds,
                CancellationToken.None));

            if (generation == m_UpdateGeneration && !m_IsRunning)
                BeginUpdateLoop();
        }

        void Initialize()
        {
            // FDT patch: replacement enumeration belongs to Reset, after old delivered IDs have been captured.
            BaseSimulationSceneManager.environmentSetupFinished -= Initialize;
            // FDT patch: invalidate any pending delayed restart and its old environment value identities.
            m_UpdateGeneration++;
            if (m_IsRunning)
                Stop();

            m_ImagesInScene.Clear();
            m_TrackingStatesOfImages.Clear();
            m_ReferenceImagesForImages.Clear();
            m_ImageDeliveries.Clear(); // FDT patch: replacement enumeration fills this retained value buffer.
            m_PreviousLibraryCount = 0;

            var origin = Object.FindAnyObjectByType<XROrigin>();

            if (origin == null)
                throw new NullReferenceException($"{nameof(SimulationImageTrackingSubsystem)} requires that the current scene contains an {nameof(XROrigin)}, but none was found.");

            var camera = origin.Camera;
            if (camera == null)
                throw new NullReferenceException($"{nameof(SimulationImageTrackingSubsystem)} requires a valid {nameof(XROrigin)} Camera, but none was set.");

            var simScene = SimulationSessionSubsystem.simulationSceneManager.environmentScene;

            if (!simScene.IsValid())
                throw new InvalidOperationException("The scene loaded for simulation is not valid.");

            var simPhysicsScene = simScene.GetPhysicsScene();

            if (!simPhysicsScene.IsValid())
                throw new InvalidOperationException("The physics scene loaded for simulation is not valid.");

            m_EnvironmentScene = simScene; // FDT patch: retain the exact environment incarnation.

            m_ImageDiscoveryStrategy = new TrackedImageDiscoveryStrategy(camera, simPhysicsScene);
            var discoveryParams = trackedImageDiscoveryParams ??
                                  XRSimulationRuntimeSettings.Instance.trackedImageDiscoveryParams;
            var trackingUpdateIntervalMilliseconds =
                (int)(discoveryParams.trackingUpdateInterval * 1000);

            if (trackingUpdateIntervalMilliseconds > 0)
                m_TrackingUpdateIntervalMilliseconds = trackingUpdateIntervalMilliseconds;

            m_PreviousLibraryCount = m_SimulationRuntimeImageLibrary?.count ?? 0;

            foreach (var rootObject in simScene.GetRootGameObjects())
            {
                foreach (var image in rootObject.GetComponentsInChildren<SimulatedTrackedImage>())
                {
                    m_ImagesInScene.Add(image);
                    m_ImageDeliveries.Add(new ImageDelivery { id = image.trackableId }); // FDT patch: cache value identity.
                    m_TrackingStatesOfImages.Add(TrackingState.None);
                    if (m_SimulationRuntimeImageLibrary != null)
                    {
                        // note: since the reference images container is a map to Nullable
                        // reference images, and the XRReferenceImage is a struct, we need
                        // to explicitly put in a 'null' in the case of failure since otherwise
                        // the reference image will have *never* get updated if/when the
                        // real reference image gets added at runtime.
                        if (m_SimulationRuntimeImageLibrary.TryGetReferenceImageWithGuid(
                                image.imageAssetGuid,
                                out var foundImage))
                            m_ReferenceImagesForImages.Add(foundImage);
                        else
                            m_ReferenceImagesForImages.Add(null);
                    }
                }
            }

            imagesInitialized?.Invoke(m_ImagesInScene.Count);
            m_Initialized = true;
            BeginUpdateLoop();
        }

        async Task UpdateTracking(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!m_Initialized)
                {
                    await RunWithoutCancellationExceptions(Task.Delay(m_TrackingUpdateIntervalMilliseconds, cancellationToken));
                    continue;
                }

                using (new ScopedProfiler("XRSimulationImageTracking"))
                {
                    var hasLibrary = m_SimulationRuntimeImageLibrary != null;
                    var currentLibraryCount = hasLibrary ? m_SimulationRuntimeImageLibrary.count : 0;
                    var hasLibraryChanged = hasLibrary && currentLibraryCount > m_PreviousLibraryCount;

                    for (var i = 0; i < m_ImagesInScene.Count; i++)
                    {
                        var image = m_ImagesInScene[i];
                        var prevTrackingState = m_TrackingStatesOfImages[i];
                        var newTrackingState = m_ImageDiscoveryStrategy.ComputeTrackingState(image);

                        if (!m_ReferenceImagesForImages[i].HasValue && hasLibraryChanged)
                        {
                            if (m_SimulationRuntimeImageLibrary.TryGetReferenceImageWithGuid(
                                    image.imageAssetGuid,
                                    out var foundImage))
                            {
                                m_ReferenceImagesForImages[i] = foundImage;
                            }
                        }

                        if (prevTrackingState is TrackingState.None && newTrackingState is TrackingState.Tracking)
                            SubsystemAddImageAtIndex(i);
                        else if (prevTrackingState is TrackingState.Tracking || newTrackingState is TrackingState.Tracking)
                            SubsystemUpdateImageAtIndex(i, newTrackingState);
                    }

                    m_PreviousLibraryCount = currentLibraryCount;
                }

                await RunWithoutCancellationExceptions(Task.Delay(m_TrackingUpdateIntervalMilliseconds, cancellationToken));
            }
        }

        void SubsystemAddImageAtIndex(int imageIndex)
        {
            var image = m_ImagesInScene[imageIndex];
            // FDT patch: remember an unconsumed add separately from an already delivered image, even after tracking loss.
            var delivery = m_ImageDeliveries[imageIndex];
            delivery.pendingAdd = true;
            m_ImageDeliveries[imageIndex] = delivery;
            m_TrackingStatesOfImages[imageIndex] = TrackingState.Tracking;
            imageAdded?.Invoke(CreateXRImage(image, TrackingState.Tracking, m_ReferenceImagesForImages[imageIndex]));
        }

        void SubsystemUpdateImageAtIndex(int imageIndex, TrackingState trackingState)
        {
            var image = m_ImagesInScene[imageIndex];
            m_TrackingStatesOfImages[imageIndex] = trackingState;
            imageUpdated?.Invoke(CreateXRImage(image, trackingState, m_ReferenceImagesForImages[imageIndex]));
        }

        static XRTrackedImage CreateXRImage(SimulatedTrackedImage image, TrackingState trackingState, XRReferenceImage? referenceImage)
        {
            return new XRTrackedImage(
                trackableId: image.trackableId,
                sourceImageId: referenceImage?.guid ?? image.fallbackSourceImageId,
                pose: image.transform.GetWorldPose(),
                size: image.size,
                trackingState: trackingState,
                nativePtr: IntPtr.Zero);
        }
    }
}
