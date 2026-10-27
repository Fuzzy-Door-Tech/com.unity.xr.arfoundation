// FDT patch: regression coverage for persistent sessions whose simulation environment is unloaded.
#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine.Events;
using UnityEngine.TestTools;
using UnityEngine.XR.ARSubsystems;

namespace UnityEngine.XR.Simulation.Tests
{
    class FdtSimulationLifecycleTests : SimulationSessionTestSetup
    {
        const string Destination = "Packages/com.unity.xr.arfoundation/Tests/Runtime/XRSimulation/FdtSimulationDestination.unity";
        XRSessionSubsystem m_Session;
        XRMeshSubsystem m_Mesh;
        XRInputSubsystem m_Input;
        System.Func<SimulationSceneManager> m_PreviousSceneFactory;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            m_PreviousSceneFactory = SimulationSessionSubsystem.simulationSceneManagerFactory;
            SimulationSessionSubsystem.simulationSceneManagerFactory = CreateTestSceneManager;
            SetupSession();
            var origin = Object.FindAnyObjectByType<XROrigin>();
            Assert.That(origin, Is.Not.Null);
            Object.DontDestroyOnLoad(origin.transform.root.gameObject);
            SetupInput();
            m_Session = GetSubsystem<XRSessionSubsystem>();
            m_Input = GetSubsystem<XRInputSubsystem>();
            m_Mesh = GetSubsystem<XRMeshSubsystem>();
            Assert.That(m_Mesh, Is.Not.Null);
            m_Mesh.Start();
            FdtSimulationPlaneStartProbe.PlaneSubsystem = GetSubsystem<XRPlaneSubsystem>();
            FdtSimulationPlaneStartProbe.StartPlanes = true;
            FdtSimulationPlaneStartProbe.StartCount = 0;
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            FdtSimulationPlaneStartProbe.StartPlanes = false;
            FdtSimulationPlaneStartProbe.StartCount = 0;
            var plane = GetSubsystem<XRPlaneSubsystem>();
            if (plane.running) plane.Stop();
            var images = GetSubsystem<XRImageTrackingSubsystem>();
            if (images.running) images.Stop();
            var boxes = GetSubsystem<XRBoundingBoxSubsystem>();
            if (boxes.running) boxes.Stop();
            FdtSimulationPlaneStartProbe.PlaneSubsystem = null;
            m_Mesh.Stop();
            TearDownInput();
            TearDownSession();
            SimulationSessionSubsystem.simulationSceneManagerFactory = m_PreviousSceneFactory;
            yield return null;
        }

        [UnityTest]
        public IEnumerator SingleLoads_RebuildBeforeDestinationStart_AndRetainSubsystems()
        {
            var scanner = SimulationEnvironmentScanner.GetOrCreate();
            for (var i = 0; i < 3; i++)
            {
                var oldEnvironment = SimulationSessionSubsystem.simulationSceneManager.simulationEnvironment;
                GetSubsystem<XRPlaneSubsystem>().Stop();
                yield return Load(LoadSceneMode.Single);
                Assert.That(oldEnvironment == null, Is.True, "Single load destroys the old environment.");
                Assert.That(FdtSimulationPlaneStartProbe.StartCount, Is.EqualTo(i + 1));
                Assert.That(SimulationEnvironmentScanner.GetOrCreate(), Is.SameAs(scanner), "Providers retain this scanner.");
                AssertLiveEnvironmentAndSubsystems();
                Assert.That(GetSubsystem<XRPlaneSubsystem>().running, Is.True, "Destination Start can register planes.");
                Assert.That(GetSubsystem<XRPointCloudSubsystem>().running, Is.False, "Disabled consumers stay stopped.");
                LogAssert.NoUnexpectedReceived();
            }
        }

        [UnityTest]
        public IEnumerator AdditiveLoad_KeepsLiveEnvironmentAndSessionId()
        {
            var environment = SimulationSessionSubsystem.simulationSceneManager.simulationEnvironment;
            var sessionId = m_Session.sessionId;
            yield return Load(LoadSceneMode.Additive);
            Assert.That(SimulationSessionSubsystem.simulationSceneManager.simulationEnvironment, Is.SameAs(environment));
            Assert.That(m_Session.sessionId, Is.EqualTo(sessionId));
            AssertLiveEnvironmentAndSubsystems();
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator RestartAfterSingleLoad_RebuildsAStoppedSessionsEnvironment()
        {
            FdtSimulationPlaneStartProbe.StartPlanes = false;
            m_Session.Stop();
            yield return Load(LoadSceneMode.Single);
            m_Session.Start();
            GetSubsystem<XRPlaneSubsystem>().Start();
            yield return null;
            AssertLiveEnvironmentAndSubsystems();
            Assert.That(GetSubsystem<XRPlaneSubsystem>().running, Is.True);
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator RunningDiscovery_RemovesDeliveredOldIds_AndDiscoversReplacementObjects()
        {
            yield return CheckDiscoveryReplacement(false);
        }

        [UnityTest]
        public IEnumerator StoppedDiscovery_ReenumeratesWhenEnabledAfterReplacement()
        {
            yield return CheckDiscoveryReplacement(true);
        }

        [UnityTest]
        public IEnumerator DiscoveryResume_ReissuesUndeliveredReplacementAdditions()
        {
            yield return CheckDiscoveryReplacement(false, true);
        }

        [UnityTest]
        public IEnumerator StopFromEarlierSceneLoadedListener_DoesNotRebuildTheStoppedSession()
        {
            m_Session.Stop();
            UnityAction<Scene, LoadSceneMode> stop = (scene, mode) => m_Session.Stop();
            SceneManager.sceneLoaded += stop;
            m_Session.Start();
            FdtSimulationPlaneStartProbe.StartPlanes = false;
            try
            {
                yield return Load(LoadSceneMode.Single);
                Assert.That(m_Session.running, Is.False);
                Assert.That(SimulationSessionSubsystem.simulationSceneManager.simulationEnvironment == null, Is.True,
                    "An in-flight callback must honor Stop rather than rebuild and restart its scanner.");
            }
            finally
            {
                SceneManager.sceneLoaded -= stop;
                m_Session.Start();
            }
        }

        IEnumerator CheckDiscoveryReplacement(bool stopDuringLoad, bool pausePendingImage = false)
        {
            var origin = Object.FindAnyObjectByType<XROrigin>();
            origin.Camera.GetComponent<UnityEngine.InputSystem.XR.TrackedPoseDriver>().enabled = false;
            Object.FindAnyObjectByType<SimulationCameraPoseProvider>().enabled = false;
            SimulatedTrackedImage target = null;
            foreach (var image in SimulationSessionSubsystem.simulationSceneManager.simulationEnvironmentImages)
            {
                target = image;
                break;
            }
            Assert.That(target, Is.Not.Null, "The fixed test environment contains an image.");
            origin.Camera.transform.SetPositionAndRotation(target.transform.position + target.transform.up,
                Quaternion.LookRotation(-target.transform.up));

            var images = GetSubsystem<XRImageTrackingSubsystem>();
            images.imageLibrary = images.CreateRuntimeLibrary(null);
            images.Start();
            var boxes = GetSubsystem<XRBoundingBoxSubsystem>();
            boxes.Start();
            var oldImages = new HashSet<TrackableId>();
            var oldBoxes = new HashSet<TrackableId>();
            var deadline = Time.realtimeSinceStartupAsDouble + 5.0;
            while ((oldImages.Count == 0 || oldBoxes.Count == 0) && Time.realtimeSinceStartupAsDouble < deadline)
            {
                using (var changes = images.GetChanges(Allocator.Temp))
                    foreach (var image in changes.added) oldImages.Add(image.trackableId);
                using (var changes = boxes.GetChanges(Allocator.Temp))
                    foreach (var box in changes.added) oldBoxes.Add(box.trackableId);
                yield return null;
            }
            Assert.That(oldImages.Count, Is.GreaterThan(0), "Read at least one delivered image before scene replacement.");
            Assert.That(oldBoxes.Count, Is.GreaterThan(0), "Read delivered bounding boxes before scene replacement.");
            if (stopDuringLoad)
            {
                images.Stop();
                boxes.Stop();
            }

            yield return Load(LoadSceneMode.Single);
            if (stopDuringLoad)
            {
                images.Start();
                boxes.Start();
            }
            if (pausePendingImage)
            {
                images.Stop();
                images.Start();
            }
            var removedImages = new HashSet<TrackableId>();
            var removedBoxes = new HashSet<TrackableId>();
            var newImages = new HashSet<TrackableId>();
            var newBoxes = new HashSet<TrackableId>();
            deadline = Time.realtimeSinceStartupAsDouble + 5.0;
            while ((newImages.Count == 0 || newBoxes.Count == 0 || !oldImages.IsSubsetOf(removedImages) ||
                    !oldBoxes.IsSubsetOf(removedBoxes)) && Time.realtimeSinceStartupAsDouble < deadline)
            {
                using (var changes = images.GetChanges(Allocator.Temp))
                {
                    foreach (var id in changes.removed) removedImages.Add(id);
                    foreach (var image in changes.added) newImages.Add(image.trackableId);
                }
                using (var changes = boxes.GetChanges(Allocator.Temp))
                {
                    foreach (var id in changes.removed) removedBoxes.Add(id);
                    foreach (var box in changes.added) newBoxes.Add(box.trackableId);
                }
                yield return null;
            }
            Assert.That(oldImages.IsSubsetOf(removedImages), Is.True, "Every delivered old image must be removed.");
            Assert.That(oldBoxes.IsSubsetOf(removedBoxes), Is.True, "Every delivered old box must be removed.");
            Assert.That(newImages.Count, Is.GreaterThan(0), "Discover images from the replacement environment.");
            Assert.That(newBoxes.Count, Is.GreaterThan(0), "Discover boxes from the replacement environment.");
            Assert.That(oldImages.Overlaps(newImages) || oldBoxes.Overlaps(newBoxes), Is.False);
            LogAssert.NoUnexpectedReceived();
        }

        static SimulationSceneManager CreateTestSceneManager() => new TestSceneManager();

        sealed class TestSceneManager : SimulationSceneManager
        {
            protected override GameObject GetOrCreateEnvironmentPrefab() => AssetDatabase.LoadAssetAtPath<GameObject>(
                "Packages/com.unity.xr.arfoundation/Assets/Prefabs/DefaultSimulationEnvironment.prefab");
        }

        static IEnumerator Load(LoadSceneMode mode)
        {
            var operation = EditorSceneManager.LoadSceneAsyncInPlayMode(Destination, new LoadSceneParameters(mode));
            Assert.That(operation, Is.Not.Null);
            while (!operation.isDone) yield return null;
            yield return null;
        }

        void AssertLiveEnvironmentAndSubsystems()
        {
            var manager = SimulationSessionSubsystem.simulationSceneManager;
            Assert.That(manager.simulationEnvironment, Is.Not.Null);
            Assert.That(manager.environmentScene.GetPhysicsScene().IsValid(), Is.True);
            Assert.That(GetSubsystem<XRSessionSubsystem>(), Is.SameAs(m_Session));
            Assert.That(GetSubsystem<XRMeshSubsystem>(), Is.SameAs(m_Mesh));
            Assert.That(GetSubsystem<XRInputSubsystem>(), Is.SameAs(m_Input));
            Assert.That(m_Session.running && m_Mesh.running && m_Input.running, Is.True);
        }
    }
}
#endif
