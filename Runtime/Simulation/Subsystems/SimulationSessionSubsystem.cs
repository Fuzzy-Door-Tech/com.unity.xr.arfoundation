using System;
using Unity.Profiling; // FDT patch: attribute event-driven environment reconstruction without polling or logging.
using Unity.XR.CoreUtils;
// FDT patch: observe scene completion without polling or replacing loaded subsystem identities.
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.XR.ARSubsystems;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityEngine.XR.Simulation
{
    /// <summary>
    /// Simulation implementation of
    /// [XRSessionSubsystem](xref:UnityEngine.XR.ARSubsystems.XRSessionSubsystem).
    /// </summary>
    public sealed class SimulationSessionSubsystem : XRSessionSubsystem
    {
        internal const string k_SubsystemId = "XRSimulation-Session";

        static SimulationSceneManager s_SimulationSceneManager;

        /// <summary>
        /// This property allows for a class to establish a scene manager factory to use
        /// by this subsystem.  The scene manager factory, if set, will supply the scene
        /// manager to use by the subsystem.  If no factory is set, then the default
        /// behavior of the subsystem is to use the SimulationSceneManager.
        /// </summary>
        internal static Func<SimulationSceneManager> simulationSceneManagerFactory { get; set; }

        internal static SimulationSceneManager simulationSceneManager => s_SimulationSceneManager;

        internal static event Action s_SimulationSessionReset;

        class SimulationProvider : Provider
        {
            // FDT patch: rebuild work is observable separately from the ordinary session update.
            static readonly ProfilerMarker k_EnvironmentRebuildMarker = new("XRSimulation.EnvironmentRebuild");
            SimulationMeshSubsystem m_MeshSubsystem;

            Camera m_XROriginCamera;
            int m_PreviousCullingMask;
            bool m_Initialized;
            Guid m_SessionId;

            // FDT patch: one callback reservation for this provider's running lifetime.
            readonly UnityAction<Scene, LoadSceneMode> m_OnSceneLoaded;
            bool m_ObservingScenes;
            bool m_RebuildingEnvironment;

            public SimulationProvider()
            {
                m_OnSceneLoaded = OnSceneLoaded;
            }

            public override TrackingState trackingState => TrackingState.Tracking;

            public override Promise<SessionAvailability> GetAvailabilityAsync() =>
                Promise<SessionAvailability>.CreateResolvedPromise(SessionAvailability.Installed | SessionAvailability.Supported);

            public override Guid sessionId => m_SessionId;

            protected override bool TryInitialize()
            {
                m_SessionId = Guid.NewGuid();
                return true;
            }

            bool EnsureXROriginCameraIsSet()
            {
                if (m_XROriginCamera == null)
                {
                    var xrOrigin = Object.FindAnyObjectByType<XROrigin>();

                    if (xrOrigin == null)
                    {
                        Debug.LogError($"An XR Origin is required in the scene, none found.");
                        return false;
                    }

                    m_XROriginCamera = xrOrigin.Camera;
                }
                return m_XROriginCamera != null;
            }

            bool Initialize()
            {
                s_SimulationSceneManager ??= simulationSceneManagerFactory?.Invoke() ?? new SimulationSceneManager();

                if (SimulationMeshSubsystem.GetActiveSubsystemInstance() != null)
                {
                    m_MeshSubsystem?.Dispose();
                    m_MeshSubsystem = new SimulationMeshSubsystem();
                }

                SetupSimulation();

                SimulationEnvironmentScanner.GetOrCreate().Initialize(
                    SimulationCameraPoseProvider.GetOrCreateSimulationCameraPoseProvider(),
                    s_SimulationSceneManager.environmentScene.GetPhysicsScene(),
                    s_SimulationSceneManager.simulationEnvironment.gameObject);

                m_Initialized = true;
                return true;
            }

            public override void Start()
            {
                if (!m_Initialized && !Initialize())
                    return;

                // FDT patch: a stopped persistent session can also outlive its environment scene.
                if (!HasLiveEnvironment())
                    RebuildEnvironment();

                if (!EnsureXROriginCameraIsSet())
                {
                    Debug.LogError("Failed to find a valid XROrigin object in the scene for the session.");
                    return;
                }

#if UNITY_EDITOR
                SimulationSubsystemAnalytics.SubsystemStarted(k_SubsystemId);
#endif

                var simulationCameraPoseProvider =
                    SimulationCameraPoseProvider.GetOrCreateSimulationCameraPoseProvider();
                if (simulationCameraPoseProvider != null)
                    simulationCameraPoseProvider.Start();

                m_MeshSubsystem?.Start();
                SimulationEnvironmentScanner.GetOrCreate().Start();

                m_PreviousCullingMask = m_XROriginCamera.cullingMask;
                m_XROriginCamera.cullingMask &= ~(1 << XRSimulationRuntimeSettings.Instance.environmentLayer);

                // FDT patch: sceneLoaded runs before scene Start/settings application; keep the same providers alive.
                if (!m_ObservingScenes)
                {
                    SceneManager.sceneLoaded += m_OnSceneLoaded;
                    m_ObservingScenes = true;
                }

#if UNITY_EDITOR
                AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
#endif
            }

            public override void Stop()
            {
                // FDT patch: stopped sessions do not rebuild scenes or retain a scene callback.
                StopObservingScenes();
                if (m_XROriginCamera)
                    m_XROriginCamera.cullingMask = m_PreviousCullingMask;

                SimulationEnvironmentScanner.GetOrCreate().Stop();

                var simulationCameraPoseProvider =
                    SimulationCameraPoseProvider.GetOrCreateSimulationCameraPoseProvider();
                if (simulationCameraPoseProvider != null)
                    simulationCameraPoseProvider.Stop();

#if UNITY_EDITOR
                AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
#endif
            }

            public override void Destroy()
            {
                // FDT patch: Destroy is safe even when Stop did not complete.
                StopObservingScenes();
                SimulationCameraPoseProvider.DestroySimulationCameraPoseProvider();

                if (m_MeshSubsystem != null)
                {
                    m_MeshSubsystem.Dispose();
                    m_MeshSubsystem = null;
                }

                SimulationEnvironmentScanner.GetOrCreate().Dispose();

                if (s_SimulationSceneManager != null)
                {
                    s_SimulationSceneManager.TearDownEnvironment();
                    s_SimulationSceneManager = null;
                }

                m_Initialized = false;
            }

            public override void Reset()
            {
                if (!m_Initialized)
                    return;

                m_SessionId = Guid.NewGuid();
                s_SimulationSessionReset?.Invoke();
            }

            public override void Update(XRSessionUpdateParams updateParams)
            {
                SimulationEnvironmentScanner.GetOrCreate().Update();
            }

            void SetupSimulation()
            {
                s_SimulationSceneManager.SetupEnvironment();
                var simulationCameraPoseProvider =
                    SimulationCameraPoseProvider.GetOrCreateSimulationCameraPoseProvider();
                if (simulationCameraPoseProvider != null)
                {
                    simulationCameraPoseProvider.SetSimulationEnvironment(s_SimulationSceneManager
                        .simulationEnvironment);
                }
            }

            // FDT patch: rebuild only a missing environment. Native mesh/input, camera, and providers retain identity.
            static bool HasLiveEnvironment()
            {
                if (s_SimulationSceneManager == null)
                    return false;
                var scene = s_SimulationSceneManager.environmentScene;
                return scene.IsValid() && scene.isLoaded && s_SimulationSceneManager.simulationEnvironment != null;
            }

            void OnSceneLoaded(Scene scene, LoadSceneMode mode)
            {
                if (m_Initialized && !m_RebuildingEnvironment && !HasLiveEnvironment())
                    RebuildEnvironment();
            }

            void RebuildEnvironment()
            {
                using var marker = k_EnvironmentRebuildMarker.Auto(); // FDT patch: no timer or per-frame work added.
                m_RebuildingEnvironment = true;
                try
                {
                    var scanner = SimulationEnvironmentScanner.GetOrCreate();
                    scanner.PrepareForEnvironmentChange();
                    // Teardown must reset its static initialized flag even if Unity already unloaded the scene.
                    s_SimulationSceneManager.TearDownEnvironment();
                    SetupSimulation();
                    scanner.Initialize(SimulationCameraPoseProvider.GetOrCreateSimulationCameraPoseProvider(),
                        s_SimulationSceneManager.environmentScene.GetPhysicsScene(),
                        s_SimulationSceneManager.simulationEnvironment.gameObject);
                    Reset();
                    scanner.Start();
                }
                finally
                {
                    m_RebuildingEnvironment = false;
                }
            }

            void StopObservingScenes()
            {
                if (!m_ObservingScenes)
                    return;
                SceneManager.sceneLoaded -= m_OnSceneLoaded;
                m_ObservingScenes = false;
            }

#if UNITY_EDITOR
            static void OnBeforeAssemblyReload()
            {
                const string domainReloadOptions =
                    "either <b>Recompile After Finished Playing</b> or <b>Stop Playing and Recompile</b>";

                Debug.LogError(
                    "XR Simulation does not support script recompilation while playing. To disable script compilation"+
                    " while playing, in the Preferences window under <b>General > Script Changes While Playing</b>,"+
                    $" select {domainReloadOptions}.");
            }
#endif
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void RegisterDescriptor()
        {
            XRSessionSubsystemDescriptor.Register(new XRSessionSubsystemDescriptor.Cinfo {
                id = k_SubsystemId,
                providerType = typeof(SimulationProvider),
                subsystemTypeOverride = typeof(SimulationSessionSubsystem),
                supportsInstall = false,
                supportsMatchFrameRate = false,
            });
        }
    }
}
